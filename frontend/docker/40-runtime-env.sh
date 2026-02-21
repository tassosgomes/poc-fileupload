#!/bin/sh
set -eu

API_URL_VALUE=${API_URL:-}

if [ -z "$API_URL_VALUE" ]; then
  echo "ERROR: API_URL is not set"
  exit 1
fi

export API_URL="$API_URL_VALUE"

envsubst '${API_URL}' \
  < /usr/share/nginx/html/runtime-env.template.js \
  > /usr/share/nginx/html/runtime-env.js

rm /usr/share/nginx/html/runtime-env.template.js

echo "runtime-env.js generated successfully"
