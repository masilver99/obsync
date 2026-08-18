# Personal production deployment

The repository publishes immutable server images to GHCR from the `main` branch and version tags. Production deployment is deliberately manual: start `Deploy production` in GitHub Actions, enter the 40-character commit SHA produced by CI, and approve the `production` environment.

## One-time server setup

Create a deployment directory on the Linux host, copy `compose.production.yml` and `deploy.sh` into it, and make the script executable:

```bash
sudo mkdir -p /opt/obsync
sudo chown "$USER":"$USER" /opt/obsync
cp deploy/compose.production.yml deploy/deploy.sh /opt/obsync/
chmod 700 /opt/obsync/deploy.sh
```

Create `/opt/obsync/.env` from the repository `.env.example`. Set a long random `JWT_SIGNING_KEY`, the intended administrator credentials, and any required `CORS_ORIGINS`. Keep `REGISTRATION_KEY` set only while creating invited accounts, then remove it and restart the service. Never commit this file or the `/opt/obsync/data` directory.

The deployment user needs access to Docker and the deployment directory. Use a dedicated SSH key for this account and restrict the key and account as appropriate for the host. The deployment script does not need repository write access.

If the GHCR package is private, authenticate the server's Docker client to `ghcr.io` with a read-only package token before the first deployment. A public image is also reasonable for this public-source project because the image contains no runtime data or secrets.

## GitHub production environment

Create an environment named `production`, require a reviewer, and restrict it to the protected `main` branch or release tags. Add these environment secrets:

- `DEPLOY_HOST`: DNS name or IP of the server.
- `DEPLOY_USER`: the dedicated deployment account.
- `DEPLOY_SSH_KEY`: its private SSH key.
- `DEPLOY_KNOWN_HOSTS`: the exact, reviewed `known_hosts` line for the server.

Add this environment variable:

- `DEPLOY_PATH`: normally `/opt/obsync`.

The workflow never receives the application JWT key, administrator password, registration key, SQLite database, or vault objects.

## Backups and rollback

Before changing the container, `deploy.sh` stops the service, creates a SQLite-WAL-safe archive of the deployment data, pulls the selected immutable image, and checks `/health`. Failed health checks attempt to restore the previous image. Keep an independent off-host copy of the backup directory as well; a local backup does not protect against host loss.
