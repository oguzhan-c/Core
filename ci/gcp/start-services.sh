#!/usr/bin/env bash
# Google Cloud Build: entegrasyon servislerini adımların ortak ağı "cloudbuild"da başlatır. Sonraki adımlar
# servislere adlarıyla erişir (rabbitmq:5672, kafka:9092 ...). Derleme bitince Cloud Build container'ları kaldırır.
set -euo pipefail

docker run -d --network cloudbuild --name rabbitmq rabbitmq:4-management

# Adla erişildiği için Kafka kendini "kafka:9092" olarak duyurmalı (imajın varsayılanı localhost)
docker run -d --network cloudbuild --name kafka \
  -e KAFKA_NODE_ID=1 \
  -e KAFKA_PROCESS_ROLES=broker,controller \
  -e KAFKA_LISTENERS=PLAINTEXT://:9092,CONTROLLER://:9093 \
  -e KAFKA_ADVERTISED_LISTENERS=PLAINTEXT://kafka:9092 \
  -e KAFKA_CONTROLLER_LISTENER_NAMES=CONTROLLER \
  -e KAFKA_LISTENER_SECURITY_PROTOCOL_MAP=CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT \
  -e KAFKA_CONTROLLER_QUORUM_VOTERS=1@localhost:9093 \
  -e KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR=1 \
  -e KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR=1 \
  -e KAFKA_TRANSACTION_STATE_LOG_MIN_ISR=1 \
  -e KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS=0 \
  apache/kafka:4.3.0

docker run -d --network cloudbuild --name redis redis:7-alpine

docker run -d --network cloudbuild --name elasticsearch \
  -e ES_SETTING_DISCOVERY_TYPE=single-node \
  -e ES_SETTING_XPACK_SECURITY_ENABLED=false \
  -e "ES_JAVA_OPTS=-Xms512m -Xmx512m" \
  docker.elastic.co/elasticsearch/elasticsearch:9.5.5
