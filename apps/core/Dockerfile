# syntax=docker/dockerfile:1.7

FROM node:22-bookworm-slim AS base
ENV PNPM_HOME=/pnpm
ENV PATH=$PNPM_HOME:$PATH
RUN corepack enable
WORKDIR /repo

# --- Cài phụ thuộc, tách riêng để tận dụng cache khi mã nguồn đổi ---
FROM base AS deps
COPY pnpm-lock.yaml pnpm-workspace.yaml package.json ./
COPY packages/shared/package.json packages/shared/
COPY apps/core/package.json apps/core/
RUN --mount=type=cache,id=pnpm,target=/pnpm/store \
    pnpm install --frozen-lockfile --filter @lis/core... --prod=false

# --- Biên dịch TypeScript ---
FROM deps AS build
COPY tsconfig.base.json ./
COPY packages/shared packages/shared
COPY apps/core apps/core
RUN pnpm --filter @lis/core exec tsc -p tsconfig.json
RUN pnpm --filter @lis/shared exec tsc -p tsconfig.json

# --- Chỉ giữ phụ thuộc chạy thật ---
FROM base AS prod-deps
COPY pnpm-lock.yaml pnpm-workspace.yaml package.json ./
COPY packages/shared/package.json packages/shared/
COPY apps/core/package.json apps/core/
RUN --mount=type=cache,id=pnpm,target=/pnpm/store \
    pnpm install --frozen-lockfile --filter @lis/core... --prod

# --- Ảnh cuối ---
FROM node:22-bookworm-slim AS runtime
ENV NODE_ENV=production
# packages/shared khai báo điều kiện "production" trong "exports" để trỏ vào
# dist đã biên dịch thay vì mã nguồn .ts. NODE_ENV=production KHÔNG tự thêm
# điều kiện này vào bộ phân giải module ESM của Node — phải khai rõ qua
# --conditions, nếu không "node" sẽ rơi về "default" (./src/index.ts) và
# gãy với lỗi "Unknown file extension .ts" ngay khi khởi động.
ENV NODE_OPTIONS=--conditions=production
WORKDIR /repo

COPY --from=prod-deps /repo/node_modules node_modules
COPY --from=prod-deps /repo/apps/core/node_modules apps/core/node_modules
COPY --from=prod-deps /repo/packages/shared/node_modules packages/shared/node_modules
COPY --from=build /repo/apps/core/dist apps/core/dist
COPY --from=build /repo/packages/shared/dist packages/shared/dist
COPY apps/core/package.json apps/core/
COPY packages/shared/package.json packages/shared/

# Chạy bằng người dùng thường, không phải root.
USER node
EXPOSE 3000

HEALTHCHECK --interval=15s --timeout=3s --start-period=20s --retries=3 \
  CMD node -e "fetch('http://127.0.0.1:'+(process.env.CORE_PORT||3000)+'/health').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))"

CMD ["node", "apps/core/dist/main.js"]
