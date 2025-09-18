FROM ubuntu:20.04

# Install required packages
RUN apt-get update && \
    apt-get install -y wget gnupg && \
    wget -qO - https://package.perforce.com/perforce.pubkey | apt-key add - && \
    echo "deb https://package.perforce.com/apt/ubuntu focal release" > /etc/apt/sources.list.d/perforce.list && \
    apt-get update && \
    apt-get install -y helix-cli helix-p4d && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

# Create perforce user and directories (only if user doesn't exist)
RUN id -u perforce >/dev/null 2>&1 || useradd -m -s /bin/bash perforce && \
    mkdir -p /opt/perforce && \
    chown perforce:perforce /opt/perforce

# Switch to perforce user
USER perforce
WORKDIR /opt/perforce

# Set the default command to run the Perforce server
CMD ["p4d", "-d"]