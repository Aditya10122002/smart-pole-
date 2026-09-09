# ARGUMENTS --------------------------------------------------------------------
ARG IMAGE_ARCH=arm64
ARG BASE_VERSION=3.4-8.0.8
ARG APP_ROOT=/app
# ARGUMENTS --------------------------------------------------------------------


# BUILD ------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

ARG IMAGE_ARCH
ARG APP_ROOT

COPY . ${APP_ROOT}
WORKDIR ${APP_ROOT}

RUN dotnet restore && \
    if [ "$IMAGE_ARCH" = "arm64" ] ; then \
        export ARCH=${IMAGE_ARCH} ; \
    elif [ "$IMAGE_ARCH" = "armhf" ] ; then \
        export ARCH="arm" ; \
    elif [ "$IMAGE_ARCH" = "amd64" ] ; then \
        export ARCH="x64" ; \
    fi && \
    dotnet publish -c Release -r linux-${ARCH} --no-self-contained && \
    if [ "./bin/Release/net8.0/linux-${ARCH}" != "./bin/Release/net8.0/linux-${IMAGE_ARCH}" ]; then \
        mv ./bin/Release/net8.0/linux-${ARCH} ./bin/Release/net8.0/linux-${IMAGE_ARCH}; \
    fi
# BUILD ------------------------------------------------------------------------


# DEPLOY -----------------------------------------------------------------------
FROM --platform=linux/${IMAGE_ARCH} \
    torizon/dotnet:${BASE_VERSION} AS deploy

ARG IMAGE_ARCH
ARG APP_ROOT

RUN apt-get -y update && apt-get install -y --no-install-recommends \
        libgpiod2:arm64 \
        dbus \
        dbus-user-session \
        dbus-x11 \
    && apt-get -y install --no-install-recommends \
        alsa-utils \
        libasound2 \
        libasound2-plugins \
        network-manager \
        libglib2.0-0 \
        libnss3 \
        libnspr4 \
        ffmpeg \
    && apt-get clean && apt-get autoremove -y && rm -rf /var/lib/apt/lists/*

COPY --from=build ${APP_ROOT}/bin/Release/net8.0/linux-${IMAGE_ARCH}/publish ${APP_ROOT}

RUN mkdir -p /data && chmod 777 /data

WORKDIR ${APP_ROOT}

EXPOSE 55555 5000 2000 5003 7777 9000 65440

LABEL maintainer="BluAI Pvt. Ltd." \
      app="VitalsChair Backend" \
      arch="arm64"

CMD ["./vitalschair_prod_v1"]
# DEPLOY -----------------------------------------------------------------------
