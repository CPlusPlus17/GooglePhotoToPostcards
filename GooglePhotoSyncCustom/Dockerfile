FROM mcr.microsoft.com/dotnet/runtime:7.0-bookworm-slim AS base
WORKDIR /app
RUN apt update && apt install python3 pip -y
RUN pip install --break-system-packages postcards

FROM mcr.microsoft.com/dotnet/sdk:7.0-bookworm-slim AS build
WORKDIR /src
COPY ["GooglePhotoSyncCustom/GooglePhotoSyncCustom.csproj", "GooglePhotoSyncCustom/"]
RUN dotnet restore "GooglePhotoSyncCustom/GooglePhotoSyncCustom.csproj"
COPY . .
WORKDIR "/src/GooglePhotoSyncCustom"
RUN dotnet build "GooglePhotoSyncCustom.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "GooglePhotoSyncCustom.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "GooglePhotoSyncCustom.dll"]
