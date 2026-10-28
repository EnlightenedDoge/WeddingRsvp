#!/usr/bin/env bash
# Checks that the Turnstile secret key in .env is accepted by Cloudflare, without ever printing it.
# A dummy token is sent: a VALID secret makes Cloudflare answer "invalid-input-response" (it refused only the dummy token);
# a wrong or missing secret makes it answer "invalid-input-secret".
# The secret is passed to curl on stdin (printf is a shell builtin), never as a command-line argument, never written to a file.
set -eu
ENV_FILE="${1:-.env}"
[ -f "$ENV_FILE" ] || { echo "No $ENV_FILE here. Run this from the project folder, or pass the path: $0 /path/to/.env"; exit 2; }
get() { grep -E "^$1=" "$ENV_FILE" | head -1 | cut -d= -f2- | tr -d '\r'; }
SITE=$(get App__TurnstileSiteKey)
SECRET=$(get App__TurnstileSecretKey)
[ -n "$SITE" ] || { echo "App__TurnstileSiteKey is not set in $ENV_FILE"; exit 2; }
[ -n "$SECRET" ] || { echo "App__TurnstileSecretKey is not set in $ENV_FILE (Turnstile stays OFF until both keys are set)"; exit 2; }
echo "site key: $SITE   secret key: set (${#SECRET} characters, not shown)"

REPLY=$(printf '%s' "$SECRET" | curl -sS -m 15 -X POST https://challenges.cloudflare.com/turnstile/v0/siteverify \
  --data-urlencode 'secret@-' --data-urlencode 'response=dummy-token-for-key-check') || { echo "Could not reach Cloudflare."; exit 3; }
unset SECRET
case "$REPLY" in
  *invalid-input-secret*) echo "FAIL: Cloudflare rejected the secret key. It is wrong, or it belongs to a different widget."; exit 1 ;;
  *invalid-input-response*) echo "OK: the secret key is valid (Cloudflare refused only the dummy token, as expected)."; exit 0 ;;
  *) echo "UNEXPECTED reply from Cloudflare (first 120 characters): $(printf '%s' "$REPLY" | cut -c1-120)"; exit 3 ;;
esac
