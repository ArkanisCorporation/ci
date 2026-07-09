# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

ARG VERSION=0.0.0-local
WORKDIR /src

COPY src/Mock.Container.App/Mock.Container.App.csproj src/Mock.Container.App/
RUN --mount=type=secret,id=nuget_config,target=/root/.nuget/NuGet/NuGet.Config,required=false \
    dotnet restore src/Mock.Container.App/Mock.Container.App.csproj

COPY . .
RUN dotnet publish src/Mock.Container.App/Mock.Container.App.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    -p:Version=${VERSION} \
    -p:ContinuousIntegrationBuild=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

ARG VERSION=0.0.0-local
LABEL org.opencontainers.image.title="Mock.Container.App"
LABEL org.opencontainers.image.description="Small container fixture used to verify Arkanis CI image workflows."
LABEL org.opencontainers.image.version="${VERSION}"

ENV ASPNETCORE_URLS=http://+:5085
ENV APP_BASE_URL=http://localhost:5085

WORKDIR /app
EXPOSE 5085
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Mock.Container.App.dll"]
