# RabbitMQ 3.13 management with rabbitmq_delayed_message_exchange plugin.
# The plugin .ez must match the installed RabbitMQ version exactly.
# Release assets live at: https://github.com/rabbitmq/rabbitmq-delayed-message-exchange/releases
FROM rabbitmq:3.13-management

# Plugin version must match the RabbitMQ minor (3.13.x).
# Using 3.13.0 — last stable release for the 3.13 series.
ARG DELAYED_EXCHANGE_VERSION=3.13.0

ADD https://github.com/rabbitmq/rabbitmq-delayed-message-exchange/releases/download/v${DELAYED_EXCHANGE_VERSION}/rabbitmq_delayed_message_exchange-${DELAYED_EXCHANGE_VERSION}.ez \
    /opt/rabbitmq/plugins/

RUN rabbitmq-plugins enable --offline rabbitmq_delayed_message_exchange
