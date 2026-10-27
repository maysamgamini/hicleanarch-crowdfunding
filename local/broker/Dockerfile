# The local stack's broker set-up: Moto's image (it has boto3), the provisioning script and the plan
# it reads. Built from the repository root by compose.yaml (service broker-setup); no files are
# mounted, so it works with any Docker host.
FROM motoserver/moto:5.2.3
COPY local/broker/provision.py /broker/provision.py
COPY events/broker-subscriptions.json /broker/broker-subscriptions.json
ENTRYPOINT ["python3", "/broker/provision.py", "/broker/broker-subscriptions.json"]
