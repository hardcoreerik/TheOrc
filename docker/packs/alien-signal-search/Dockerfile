FROM python:3.11-slim

ARG TURBO_SETI_COMMIT=7d9b4fde9bc98d834dc11cfc0acd2380e6676f0e
ARG BLIMPY_COMMIT=3ebf04342227a95405aa32e5bc75832d1dd17f28

RUN useradd --create-home --uid 10001 worker && \
    pip install --no-cache-dir \
      "https://github.com/UCBerkeleySETI/blimpy/archive/${BLIMPY_COMMIT}.tar.gz" \
      "https://github.com/UCBerkeleySETI/turbo_seti/archive/${TURBO_SETI_COMMIT}.tar.gz"

COPY entrypoint.py /opt/theorc/entrypoint.py
RUN chmod 0555 /opt/theorc/entrypoint.py && mkdir /output && chown worker:worker /output

USER worker
ENTRYPOINT ["python", "/opt/theorc/entrypoint.py"]
