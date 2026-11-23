FROM ubuntu:24.04

RUN apt-get update \
    && apt-get upgrade -y \
    && apt-get install -y --no-install-recommends ffmpeg fontconfig fonts-inter ca-certificates python3 python3-venv \
    && rm -rf /var/lib/apt/lists/*

RUN python3 -m venv /opt/kairos-venv
ENV PATH="/opt/kairos-venv/bin:$PATH"

WORKDIR /app
COPY pyproject.toml LICENSE NOTICE ./
COPY clipper/ ./clipper/
COPY assets/ ./assets/
COPY config/ ./config/
# Instalación editable: el código busca assets/, config/ y los prompts junto al paquete.
RUN pip install --no-cache-dir --upgrade "pip>=26.2" \
    && pip install --no-cache-dir -e ".[postgres]"

ENV CLIPPER_DATA_DIR=/data \
    CLIPPER_HOST=0.0.0.0 \
    CLIPPER_INPUT_DIR=/input \
    HF_HOME=/data/hf \
    PYTHONUNBUFFERED=1
EXPOSE 8000
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s CMD python -c "import urllib.request as u;u.urlopen('http://localhost:8000/healthz')"
ENTRYPOINT ["python", "-m", "clipper"]
CMD ["web", "--host", "0.0.0.0", "--port", "8000"]
