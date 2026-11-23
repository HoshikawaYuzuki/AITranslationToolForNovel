# Secure, hardened Alpine base without Go stdlib CVEs
FROM alpine:3.21

# 1. Update all base packages to fix known Alpine OS vulnerabilities
RUN apk update && apk upgrade --no-cache

# 2. Install native Alpine PostgreSQL 17 server, client, contrib, su-exec, tzdata, and bash
RUN apk add --no-cache \
    postgresql17 \
    postgresql17-contrib \
    postgresql17-client \
    su-exec \
    tzdata \
    bash \
    && rm -rf /var/cache/apk/*

# 3. Create postgres system user & group (UID/GID 70 matching official image)
RUN set -eux; \
    addgroup -g 70 -S postgres 2>/dev/null || true; \
    adduser -u 70 -S -D -G postgres -h /var/lib/postgresql postgres 2>/dev/null || true

# 4. Copy docker-entrypoint script and setup su-exec as gosu drop-in replacement
COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod +x /usr/local/bin/docker-entrypoint.sh \
    && ln -s /sbin/su-exec /usr/local/bin/gosu

# 5. Configure environment and directories
ENV PGDATA=/var/lib/postgresql/data \
    LANG=en_US.utf8

RUN mkdir -p /var/lib/postgresql/data /docker-entrypoint-initdb.d /run/postgresql \
    && chown -R postgres:postgres /var/lib/postgresql /run/postgresql

VOLUME /var/lib/postgresql/data

ENTRYPOINT ["docker-entrypoint.sh"]
EXPOSE 5432
CMD ["postgres"]
