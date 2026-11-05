FROM ghcr.io/bubuntux/nordlynx:latest

# On modern Linux hosts (e.g. Fedora 44, kernel 6.x) using nf_tables backend,
# legacy iptables is not functional. Symlink to iptables-nft.
RUN ln -sf /usr/sbin/iptables-nft /usr/sbin/iptables && \
    ln -sf /usr/sbin/ip6tables-nft /usr/sbin/ip6tables && \
    ln -sf /usr/sbin/iptables-nft-restore /usr/sbin/iptables-restore && \
    ln -sf /usr/sbin/iptables-nft-save /usr/sbin/iptables-save
