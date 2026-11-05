# syntax=docker/dockerfile:1

# Keep the SDK in the feature band selected by global.json. Pin the image as well
# as the SDK version so rebuilding does not silently select another toolchain.
FROM mcr.microsoft.com/dotnet/sdk:10.0.100-noble@sha256:c7445f141c04f1a6b454181bd098dcfa606c61ba0bd213d0a702489e5bd4cd71 AS toolchain

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true

RUN apt-get update \
 && apt-get install -y --no-install-recommends python3 libatomic1 \
 && rm -rf /var/lib/apt/lists/*

# Use the manifests shipped with the pinned SDK, without fetching newer versions.
RUN dotnet workload install wasm-tools --skip-manifest-update

FROM toolchain AS restore
WORKDIR /src

# BuildKit bind mounts are read-only. Restore only needs the project definitions;
# keeping this separate lets source edits reuse the restored dependencies.
RUN --mount=type=bind,source=global.json,target=/src/global.json \
    --mount=type=bind,source=Directory.Build.props,target=/src/Directory.Build.props \
    --mount=type=bind,source=WaveDudeNext/WaveDudeNext.csproj,target=/src/WaveDudeNext/WaveDudeNext.csproj \
    --mount=type=bind,source=WaveDudeNext.Wasm/WaveDudeNext.Wasm.csproj,target=/src/WaveDudeNext.Wasm/WaveDudeNext.Wasm.csproj \
    --mount=type=cache,id=wavedude-nuget,target=/root/.nuget/packages \
    dotnet restore WaveDudeNext.Wasm/WaveDudeNext.Wasm.csproj \
      -p:ArtifactsPath=/build/artifacts -warnaserror

FROM restore AS build

# Sources stay mounted read-only; intermediates, publish output and Vite's bundle
# all go to container-local paths, never into the host's bin/ or obj/ directories.
RUN --mount=type=bind,source=global.json,target=/src/global.json \
    --mount=type=bind,source=Directory.Build.props,target=/src/Directory.Build.props \
    --mount=type=bind,source=.editorconfig,target=/src/.editorconfig \
    --mount=type=bind,source=WaveDudeNext,target=/src/WaveDudeNext \
    --mount=type=bind,source=WaveDudeNext.Wasm,target=/src/WaveDudeNext.Wasm \
    --mount=type=cache,id=wavedude-nuget,target=/root/.nuget/packages \
    dotnet publish WaveDudeNext.Wasm/WaveDudeNext.Wasm.csproj \
      -c Release --no-restore -warnaserror \
      -p:ArtifactsPath=/build/artifacts -p:ViteWasmDir=/out/

# Export only the complete runtime bundle with --output type=local,dest=... .
FROM scratch AS wasm
COPY --from=build /out/ /
