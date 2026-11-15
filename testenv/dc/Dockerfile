# DEV/TEST ONLY: a Samba Active Directory domain controller for the DomainNotice test environment.
FROM ubuntu:24.04
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
 && apt-get install -y --no-install-recommends samba samba-ad-dc samba-ad-provision samba-dsdb-modules samba-vfs-modules \
      winbind krb5-user ldb-tools ldap-utils smbclient dnsutils ca-certificates openssl \
 && rm -rf /var/lib/apt/lists/* \
 && mv /etc/samba/smb.conf /etc/samba/smb.conf.dist
COPY entrypoint.sh seed.sh /usr/local/bin/
RUN chmod 755 /usr/local/bin/entrypoint.sh /usr/local/bin/seed.sh
EXPOSE 53 88 389 636 445
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
