FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/WeddingRsvp/WeddingRsvp.csproj src/WeddingRsvp/
RUN dotnet restore src/WeddingRsvp/WeddingRsvp.csproj
COPY src/ src/
RUN dotnet publish src/WeddingRsvp/WeddingRsvp.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# The app binds its own two ports (App__PublicPort / App__AdminPort); clear the image's default 8080 binding.
ENV App__DataDir=/data \
    ASPNETCORE_HTTP_PORTS= \
    TZ=Asia/Jerusalem
VOLUME /data
EXPOSE 8080 8081
ENTRYPOINT ["dotnet", "WeddingRsvp.dll"]
