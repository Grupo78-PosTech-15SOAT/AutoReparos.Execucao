# Tópico dos eventos da Execução (D-67, porta 1 de infra-ci-observabilidade): a Execução publica cada evento uma vez,
# com o atributo de mensagem `tipo`; cada consumidor assina a própria fila no Terraform dele, com raw delivery e
# filtro por `tipo` (OS: fila da Saga; Cadastros: notificações). O ARN chega ao pod em `Sns__EventosTopicArn`.
resource "aws_sns_topic" "eventos" {
  name              = "autoreparos-execucao-eventos"
  kms_master_key_id = "alias/aws/sns"
}
