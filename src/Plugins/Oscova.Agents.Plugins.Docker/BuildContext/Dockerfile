# Debian 12 slim with Node 22 (official Docker Hub image)
FROM node:22-bookworm-slim

# PIP_BREAK_SYSTEM_PACKAGES lets agents `pip install` into this disposable container
# without hitting Debian's PEP 668 externally-managed-environment guard.
ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_BREAK_SYSTEM_PACKAGES=1 \
    DEBIAN_FRONTEND=noninteractive

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        wget \
        git \
        git-lfs \
        openssh-client \
        jq \
        zip \
        unzip \
        xz-utils \
        ripgrep \
        fd-find \
        python3 \
        python3-pip \
        python3-venv \
        python-is-python3 \
        procps \
 # Debian names the fd binary fdfind; expose it under the name agents expect.
 && ln -s "$(command -v fdfind)" /usr/local/bin/fd \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /workspace/default
