#!/bin/bash
# Provisions the CORP.TEST domain on first start, seeds test objects, then runs samba in the foreground.
set -euo pipefail
REALM="${REALM:-CORP.TEST}"
DOMAIN="${DOMAIN:-CORP}"
ADMIN_PASSWORD="${ADMIN_PASSWORD:?ADMIN_PASSWORD is required}"

if [[ ! -f /var/lib/samba/private/sam.ldb ]]; then
    echo "==> provisioning $REALM"
    rm -f /etc/samba/smb.conf
    samba-tool domain provision --realm="$REALM" --domain="$DOMAIN" --server-role=dc \
        --dns-backend=SAMBA_INTERNAL --adminpass="$ADMIN_PASSWORD" --use-rfc2307 \
        --option="dns forwarder = 8.8.8.8" --option="tls enabled = yes" --option="dns update command = /bin/true"
    cp /var/lib/samba/private/krb5.conf /etc/krb5.conf
    samba-tool domain passwordsettings set --complexity=off --history-length=0 --min-pwd-age=0 --max-pwd-age=0 --min-pwd-length=8
    /usr/local/bin/seed.sh
    cp /etc/samba/smb.conf /var/lib/samba/smb.conf.provisioned
else
    cp /var/lib/samba/smb.conf.provisioned /etc/samba/smb.conf
    cp /var/lib/samba/private/krb5.conf /etc/krb5.conf
fi

echo "==> starting samba"
exec samba --foreground --no-process-group --debug-stdout
