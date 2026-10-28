# Deploying on TrueNAS Scale (24.10 or newer) as a custom app

The app is published as a Docker image by GitHub Actions (`.github/workflows/publish-image.yml`). TrueNAS pulls it, so nothing is built on the NAS.
Everything configurable is an environment variable. The data (`rsvp.db`, `media/`, `keys/`) lives in one dataset.

## 1. Publish the image (once, then on every push to `main`)
1. Push the repo. The **Publish image** workflow runs the tests, then builds and pushes `ghcr.io/<your-github-user-lowercase>/wedding-rsvp` (tags `latest` and `sha-<short>`).
   Watch it under the repo's **Actions** tab. If the tests fail, nothing is published.
2. Let TrueNAS pull it. Pick one:
   - **Make the package public** (GitHub profile, Packages, `wedding-rsvp`, Package settings, Change visibility). Simplest. The image contains only the app, no secrets.
   - **Keep it private** and log in once from the TrueNAS shell: `docker login ghcr.io -u <github-user>` with a token that has `read:packages`.

## 2. Create the dataset
1. Storage / Datasets: add a dataset, e.g. `<pool>/apps/wedding-rsvp`.
2. Edit its permissions: owner user **apps** and group **apps** (uid/gid 568), applied recursively. The container runs as 568:568 and must be able to write here.

## 3. (Optional) move the existing data
Skip this to start fresh: the first start creates the site admin from the bootstrap variables and you recreate the couple.
To keep what you have, **stop the old instance first** (`docker compose down` on the old machine). Two copies running the same queue would each send the SMS.
Then copy the old `data/` folder contents (`rsvp.db`, and `rsvp.db-wal` / `rsvp.db-shm` if present, `media/`, `keys/`) into the dataset, for example with `scp` or an SMB share,
and make sure they are owned by apps:apps. Keep `keys/`: it holds the session keys, so signed-in users and set-password links survive the move.

## 4. Install the custom app
Apps, Discover Apps, **Custom App** (named "Install via YAML" or has a "Custom Config" option; otherwise use the form with the same values).
Paste `deploy/truenas/wedding-rsvp.app.yaml` and fill in every `<placeholder>`:

| Form field | Value |
|---|---|
| Image | `ghcr.io/<your-github-user-lowercase>/wedding-rsvp` , tag `latest` |
| User and group | `568` and `568` |
| Ports (host to container) | `9080` to `8080` (public), `9081` to `8081` (private admin). Any free host ports work |
| Storage (host path) | your dataset path, mounted at `/data` |
| Environment | the `App__*` and `Sms__*` entries from the YAML |

Your SMS4FREE password, API key and Turnstile secret key are stored in the app's configuration on the NAS, so limit who can open it.

**Turnstile (optional bot protection).** Set `App__TurnstileSiteKey` and `App__TurnstileSecretKey` from your Cloudflare Turnstile widget to turn it on; leave both empty to keep it off.
It guards the couples' sign-in pages and the invitation's "seen" report on the public port, never the private admin port. In the widget's settings add your public host
(the host in `App__PublicBaseUrl`) to the allowed hostnames; if guests reach the site on other hostnames too, list them in `App__TurnstileAllowedHostnames`.
It works only when the keys belong to that widget, and Cloudflare's script must be reachable from guests' phones, which it normally is.

## 5. Point nginx at it
- **Public host** (e.g. `example.com`), with SSL: `http://<truenas-ip>:9080`. Guests and couples use it. Set `client_max_body_size 10m;` (invitation images are up to 10 MB)
  and let POSTs through to `/webhooks/sms-delivery/...` (delivery reports). It must forward `X-Forwarded-Proto` and `X-Forwarded-For` (Nginx Proxy Manager does).
- **Private admin** (site admin only): `http://<truenas-ip>:9081`, reachable from your LAN or VPN **only, never the internet**.
  With `App__AdminAllowPlainHttp: "true"` you can open `http://<truenas-ip>:9081/signin` directly on the LAN, or put it behind a LAN-only HTTPS host.

## 6. Check it works
- `https://<public host>/healthz` returns `ok`; `https://<public host>/signin` loads; `https://<public host>/admin/site/couples` returns 404.
- Sign in as the site admin on the private port, turn on two-factor authentication, then **remove `App__BootstrapAdminPassword`** from the app's environment and redeploy.
- Send one test message from the Messages page and confirm the status reaches Delivered (the delivery report came back through nginx).
- If Turnstile is on: open `https://<public host>/signin` and check the challenge appears and a sign-in still works. If it blocks you, empty both Turnstile keys and redeploy to turn it off while you check the widget's hostname settings.

## 7. Updating, rolling back, backing up
- **Update:** push to `main`, wait for the workflow to finish (check the package has a new `sha-<short>` tag), then open the app in TrueNAS, **Edit**, and **Save** to redeploy.
  `pull_policy: always` in the YAML makes the redeploy fetch the new `latest`; without that line Docker keeps the copy it already has and nothing updates.
  Database migrations run on start. Take a dataset snapshot first.
  To see what is running: `docker inspect <image>:latest --format '{{index .Config.Labels "org.opencontainers.image.revision"}}'` prints the commit it was built from.
- **Roll back:** change the image tag to an earlier `sha-<short>` and redeploy. (A migration that already ran stays; restore the snapshot if you need the old schema too.)
- **Back up:** Data Protection, Periodic Snapshot Tasks on the dataset (daily), and replicate off the NAS. The snapshot includes `keys/`.

## Before the old machine is retired
Confirm the new app works end to end, switch nginx to it, and leave the old container stopped. Do not run both against copies of the same data.
