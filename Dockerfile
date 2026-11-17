# One Dockerfile for all three services. docker-compose passes the project name:
#   docker build --build-arg PROJECT=Bookings.Api -t bookings-api .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG PROJECT
WORKDIR /src

# Copy project files first so "dotnet restore" is cached until dependencies change.
COPY Directory.Build.props ./
COPY src/StudioBooking.Contracts/*.csproj src/StudioBooking.Contracts/
COPY src/StudioBooking.Domain/*.csproj src/StudioBooking.Domain/
COPY src/StudioBooking.ServiceDefaults/*.csproj src/StudioBooking.ServiceDefaults/
COPY src/${PROJECT}/*.csproj src/${PROJECT}/
RUN dotnet restore src/${PROJECT}/${PROJECT}.csproj

COPY src/ src/
RUN dotnet publish src/${PROJECT}/${PROJECT}.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
ARG PROJECT
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080 \
    Logging__Format=json \
    APP_DLL=${PROJECT}.dll
EXPOSE 8080

# The .NET 8 images ship a non-root "app" user.
USER app
ENTRYPOINT ["sh", "-c", "exec dotnet \"$APP_DLL\""]
