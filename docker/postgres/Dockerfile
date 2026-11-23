# Builder stage to compile native C su-exec as drop-in replacement for gosu
FROM debian:13-slim AS builder

RUN apt-get update && apt-get install -y --no-install-recommends gcc libc6-dev
COPY su-exec.c /tmp/su-exec.c
RUN gcc -Wall -O2 /tmp/su-exec.c -o /tmp/gosu && strip /tmp/gosu

# Hardened PostgreSQL 17 on Debian 13 Slim (Trixie)
FROM debian:13-slim

ENV PG_MAJOR=17 \
    PGDATA=/var/lib/postgresql/data \
    LANG=en_US.utf8

# 1. Update OS packages and install PostgreSQL 17 from official Debian Trixie repo
RUN apt-get update \
    && apt-get upgrade -y \
    && apt-get install -y --no-install-recommends \
       postgresql-17 \
       postgresql-contrib-17 \
       postgresql-client-17 \
       locales \
    && sed -i -e 's/# en_US.UTF-8 UTF-8/en_US.UTF-8 UTF-8/' /etc/locale.gen \
    && locale-gen \
    && update-locale LANG=en_US.UTF-8 \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

# 2. Install native C gosu binary (su-exec) to eliminate Go stdlib CVEs
COPY --from=builder /tmp/gosu /usr/local/bin/gosu
RUN chmod +x /usr/local/bin/gosu

# 3. Copy official postgres entrypoint scripts from postgres:17-trixie
COPY --from=postgres:17-trixie /usr/local/bin/docker-* /usr/local/bin/
RUN chmod +x /usr/local/bin/docker-*

# 4. Add PostgreSQL binary path to PATH
ENV PATH="/usr/lib/postgresql/17/bin:${PATH}"

# 5. Create directories with postgres permissions
RUN mkdir -p /var/lib/postgresql/data /docker-entrypoint-initdb.d /var/run/postgresql \
    && chown -R postgres:postgres /var/lib/postgresql /var/run/postgresql

VOLUME /var/lib/postgresql/data

ENTRYPOINT ["docker-entrypoint.sh"]
EXPOSE 5432
CMD ["postgres"]
