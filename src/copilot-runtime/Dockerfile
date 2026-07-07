FROM oven/bun:1 AS build

WORKDIR /app
COPY package.json bun.lock ./
RUN --mount=type=cache,target=/root/.bun/install/cache bun install --frozen-lockfile
COPY . .

FROM oven/bun:1 AS prod-deps

WORKDIR /app
COPY package.json bun.lock ./
RUN --mount=type=cache,target=/root/.bun/install/cache bun install --frozen-lockfile --production

FROM oven/bun:1 AS runtime

WORKDIR /app
COPY --from=prod-deps /app/node_modules ./node_modules
COPY . .

ENV NODE_ENV=production

USER bun

ENTRYPOINT ["bun","server.ts"]
