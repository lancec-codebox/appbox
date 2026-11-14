# IndexNode GPU node (0.6.1): CUDA 12.x + cuDNN 9, which is what Microsoft.ML.OnnxRuntime.Gpu.Linux 1.23.x is built for
# (ONNX Runtime 1.23/1.24 wants CUDA 12.8 + cuDNN 9; CUDA 13 builds start with ORT 1.27, so do not move the base image to CUDA 13 without moving the package).
#
#   docker build -t indexnode-gpu -f indexnode/Dockerfile indexnode
#   docker run --rm --gpus all -e INDEXNODE_SAS_URL=... indexnode-gpu            # runs: indexnode run --gpu --node-name gpu-a100-westus3 --exit-when-idle 10m
#
# Models (SigLIP fp16, SSCD fp32, face and OCR models, about 0.9 GB) are downloaded and sha256-checked on first use into /models,
# or baked in with --build-arg BAKE_MODELS=1 (a bigger image, a faster cold start: worth it for an on-demand job).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY IndexNode/IndexNode.csproj IndexNode/
RUN dotnet restore IndexNode/IndexNode.csproj -r linux-x64 -p:IndexNodeGpu=true
COPY IndexNode/ IndexNode/
ARG VERSION=0.6.1
RUN dotnet publish IndexNode/IndexNode.csproj -c Release -r linux-x64 --self-contained false -p:IndexNodeGpu=true -p:Version=${VERSION} -o /out

FROM nvidia/cuda:12.8.1-cudnn-runtime-ubuntu24.04
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl libicu74 xz-utils \
 && rm -rf /var/lib/apt/lists/*
# the .NET 10 runtime (framework-dependent build: smaller than self-contained)
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
 && bash /tmp/dotnet-install.sh --channel 10.0 --runtime dotnet --install-dir /usr/share/dotnet \
 && ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet && rm /tmp/dotnet-install.sh
COPY --from=build /out /app
ENV INDEXNODE_CONFIG_DIR=/models \
    INDEXNODE_DEVICE=cuda \
    NVIDIA_VISIBLE_DEVICES=all \
    NVIDIA_DRIVER_CAPABILITIES=compute,utility \
    DOTNET_gcServer=0 \
    DOTNET_EnableDiagnostics=0
# the pinned ffmpeg (with libsvtav1/libdav1d) and, optionally, every model
ARG BAKE_MODELS=0
RUN mkdir -p /models && chmod 1777 /models && dotnet /app/indexnode.dll tools install --ffmpeg \
 && if [ "$BAKE_MODELS" = "1" ]; then dotnet /app/indexnode.dll tools install --models; fi \
 && chmod -R a+rwX /models
WORKDIR /app
# INDEXNODE_SAS_URL comes from the job's secret; INDEXNODE_NODE_NAME names the node in the registry
ENTRYPOINT ["dotnet", "/app/indexnode.dll"]
CMD ["run", "--gpu", "--node-name", "gpu-a100-westus3", "--exit-when-idle", "10m"]
