# STAGE01 - Build application and its dependencies
FROM mcr.microsoft.com/dotnet/core/sdk:3.1-alpine AS build
WORKDIR /app

COPY . ./
RUN dotnet restore

# STAGE02 - Publish the application
FROM build AS publish
WORKDIR /app/Net.Bluewalk.AnyseeTranscode
RUN dotnet publish -c Release -o ../out
RUN rm ../out/*.pdb

# STAGE03 - Create the final image
FROM mcr.microsoft.com/dotnet/core/aspnet:3.1-alpine AS runtime
LABEL Description="Anysee Transcode" \
      Maintainer="Bluewalk"

RUN apk add --no-cache ffmpeg

WORKDIR /app
COPY --from=publish /app/out ./

CMD ["dotnet", "Net.Bluewalk.AnyseeTranscode.dll"]