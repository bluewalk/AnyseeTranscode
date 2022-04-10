# STAGE01 - Build application and its dependencies
FROM mcr.microsoft.com/dotnet/sdk:6.0-alpine AS build
WORKDIR /app

COPY . ./
RUN dotnet restore

# STAGE02 - Publish the application
FROM build AS publish
WORKDIR /app/Net.Bluewalk.AnyseeTranscode
RUN dotnet publish -c Release -o ../out
RUN rm ../out/*.pdb

# STAGE03 - Create the final image
FROM mcr.microsoft.com/dotnet/aspnet:6.0-alpine AS runtime
LABEL Description="Anysee Transcode" \
      Maintainer="Bluewalk"

RUN apk add --no-cache ffmpeg icu-libs curl

WORKDIR /app
COPY --from=publish /app/out ./

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
ENV ASPNETCORE_URLS=http://+:80

EXPOSE 80

HEALTHCHECK CMD curl -sS http://localhost/status || exit 1

CMD ["dotnet", "Net.Bluewalk.AnyseeTranscode.dll"]