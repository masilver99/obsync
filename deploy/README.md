# Personal production deployment

The repository publishes immutable server images to GHCR from the `main` branch and version tags. Production deployment is deliberately manual: start `Deploy production` in GitHub Actions, enter the 40-character commit SHA produced by CI, and approve the `production` environment.

## One-time server setup

Create a root-owned deployment directory on the Linux host, copy `compose.production.yml` and `deploy.sh` into it, and make the script executable. The SSH deployment account should not be placed in the `docker` group: access to the Docker socket is effectively root access. Instead, install `obsync-deploy-ssh`, `obsync-deploy-root`, and `obsync-deploy-sudoers` from this directory, and authorize its key with a forced command:

```bash
sudo mkdir -p /opt/obsync
sudo install -o root -g root -m 700 deploy/compose.production.yml /opt/obsync/compose.production.yml
sudo install -o root -g root -m 700 deploy/deploy.sh /opt/obsync/deploy.sh
sudo install -o root -g root -m 755 deploy/obsync-deploy-ssh /usr/local/sbin/obsync-deploy-ssh
sudo install -o root -g root -m 755 deploy/obsync-deploy-root /usr/local/sbin/obsync-deploy-root
sudo install -o root -g root -m 440 deploy/obsync-deploy-sudoers /etc/sudoers.d/obsync-deploy
sudo visudo -cf /etc/sudoers.d/obsync-deploy
```

Create a system user with no password and a root-owned `authorized_keys` file. The key entry should include `command="/usr/local/sbin/obsync-deploy-ssh",no-port-forwarding,no-agent-forwarding,no-X11-forwarding,no-pty,no-user-rc`. The account has no interactive shell through that key and can invoke only the validated deployment wrapper.

Create `/opt/obsync/.env` from the repository `.env.example`. Set a long random `JWT_SIGNING_KEY`, the intended administrator credentials, and any required `CORS_ORIGINS`. Keep `REGISTRATION_KEY` set only while creating invited accounts, then remove it and restart the service. Never commit this file or the `/opt/obsync/data` directory.

The deployment user does not need direct Docker access or repository write access. Keep `/opt/obsync/.env` root-owned and mode `0600`; the root wrapper reads it through `deploy.sh`.

If the GHCR package is private, authenticate the server's Docker client to `ghcr.io` with a read-only package token before the first deployment. A public image is also reasonable for this public-source project because the image contains no runtime data or secrets.

## GitHub production environment

Create an environment named `production`, require a reviewer, and restrict it to the protected `main` branch or release tags. Add these environment secrets:

- `DEPLOY_HOST`: DNS name or IP of the server.
- `DEPLOY_USER`: the dedicated deployment account.
- `DEPLOY_SSH_KEY`: its private SSH key.
- `DEPLOY_KNOWN_HOSTS`: the exact, reviewed `known_hosts` line for the server.

The server wrapper fixes the deployment directory at `/opt/obsync`. The workflow never receives the application JWT key, administrator password, registration key, SQLite database, or vault objects.

## Backups and rollback

Before changing the container, `deploy.sh` stops the service, creates a SQLite-WAL-safe archive of the deployment data, pulls the selected immutable image, and checks `/health`. Failed health checks attempt to restore the previous image. Keep an independent off-host copy of the backup directory as well; a local backup does not protect against host loss.
