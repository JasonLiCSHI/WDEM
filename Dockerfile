# Stage 1: Build the current WDEM solution
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Cache optimization: Copy project files and restore dependencies first
COPY Wdem.sln ./
COPY src/Wdem.Core/Wdem.Core.csproj src/Wdem.Core/
COPY src/Wdem.LegacySource/Wdem.LegacySource.csproj src/Wdem.LegacySource/
COPY tests/Wdem.Core.Tests/Wdem.Core.Tests.csproj tests/Wdem.Core.Tests/
COPY tests/Wdem.LegacySource.Tests/Wdem.LegacySource.Tests.csproj tests/Wdem.LegacySource.Tests/
RUN dotnet restore Wdem.sln

# Copy remaining source code and build the transition libraries
COPY src/ src/
COPY tests/ tests/
RUN dotnet build Wdem.sln -c Release --no-restore

# Stage 2: Artifact Export Layer
# Product hosts are not present yet; this stage exports the validated build outputs.
FROM scratch AS artifact
COPY --from=build /src/src/Wdem.Core/bin/Release/ /artifacts/Wdem.Core/
COPY --from=build /src/src/Wdem.LegacySource/bin/Release/ /artifacts/Wdem.LegacySource/
