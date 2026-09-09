# ========================
# Build Args
# ========================
ARG IMAGE_ARCH=arm64
ARG APP_ROOT=/app

# ========================
# BUILD STAGE
# ========================
FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/sdk:8.0 AS build

ARG IMAGE_ARCH
ARG APP_ROOT

WORKDIR /src
COPY ./src .

RUN dotnet restore && \
    dotnet publish -c Release -r linux-${IMAGE_ARCH} --no-self-contained -o /publish

# ========================
# RUNTIME STAGE
# ========================
FROM --platform=linux/${IMAGE_ARCH} mcr.microsoft.com/dotnet/runtime:8.0

ARG APP_ROOT
WORKDIR ${APP_ROOT}

# ---- Install runtime dependencies ----
RUN apt-get update && \
    apt-get install -y \
        libgpiod-dev \
        alsa-utils \
        libasound2 \
        libasound2-plugins \
        openssl \
        rsync \
        file && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

# ---- Copy app ----
COPY --from=build /publish ${APP_ROOT}

RUN chmod +x ${APP_ROOT}/vitaldata

EXPOSE 55555
ENTRYPOINT ["./vitaldata"]
