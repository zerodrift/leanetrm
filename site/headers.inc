add_header Content-Security-Policy "default-src 'none'; style-src 'self'; img-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'" always;
add_header X-Content-Type-Options nosniff always;
add_header Referrer-Policy same-origin always;
add_header Strict-Transport-Security "max-age=31536000" always;
