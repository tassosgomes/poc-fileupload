FROM node:20-alpine AS builder
WORKDIR /app

COPY package*.json ./
RUN npm ci

COPY . .
RUN npm run build

FROM nginx:alpine AS runtime

RUN apk add --no-cache gettext

WORKDIR /usr/share/nginx/html

COPY --from=builder /app/dist ./
COPY --from=builder /app/public/runtime-env.template.js ./runtime-env.template.js
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY docker/40-runtime-env.sh /docker-entrypoint.d/40-runtime-env.sh

RUN chmod +x /docker-entrypoint.d/40-runtime-env.sh

EXPOSE 80

CMD ["nginx", "-g", "daemon off;"]
