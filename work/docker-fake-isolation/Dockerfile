FROM ubuntu:24.04

COPY fake-agent.sh /usr/local/bin/fake-agent.sh
RUN sed -i 's/\r$//' /usr/local/bin/fake-agent.sh \
    && chmod 0555 /usr/local/bin/fake-agent.sh \
    && install -d -o 65532 -g 65532 -m 0700 /read-only-probe

ENTRYPOINT ["/usr/local/bin/fake-agent.sh"]
