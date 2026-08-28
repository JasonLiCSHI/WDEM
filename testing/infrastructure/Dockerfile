# WDEM solution validation image
FROM mcr.microsoft.com/dotnet/sdk:10.0-windowsservercore-ltsc2022 AS build

WORKDIR /src

# Copy only project files first for better layer caching.
COPY Wdem.sln ./
COPY src/Wdem.Core/Wdem.Core.csproj src/Wdem.Core/
COPY src/Wdem.LegacySource/Wdem.LegacySource.csproj src/Wdem.LegacySource/
COPY tests/Wdem.Core.Tests/Wdem.Core.Tests.csproj tests/Wdem.Core.Tests/
COPY tests/Wdem.LegacySource.Tests/Wdem.LegacySource.Tests.csproj tests/Wdem.LegacySource.Tests/

# Restore, build, and test the current solution. Product hosts are not available yet.
RUN dotnet restore Wdem.sln

COPY src/ src/
COPY tests/ tests/
RUN dotnet build Wdem.sln -c Release --no-restore
RUN dotnet test Wdem.sln -c Release --no-build
