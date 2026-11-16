# Identidade AWS dos pods do serviço (IRSA): só o ServiceAccount execucao do namespace execucao assume a role.
resource "aws_iam_role" "irsa" {
  name                 = "autoreparos-${local.servico}-irsa"
  description          = "IRSA do ServiceAccount ${local.servico}/${local.servico}"
  permissions_boundary = "arn:aws:iam::${local.conta}:policy/autoreparos-servico-boundary"
  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Federated = data.aws_ssm_parameter.oidc_provider_arn.insecure_value }
      Action    = "sts:AssumeRoleWithWebIdentity"
      Condition = {
        StringEquals = {
          "${data.aws_ssm_parameter.oidc_provider_url.insecure_value}:sub" = "system:serviceaccount:${local.servico}:${local.servico}"
          "${data.aws_ssm_parameter.oidc_provider_url.insecure_value}:aud" = "sts.amazonaws.com"
        }
      }
    }]
  })
}

resource "aws_iam_role_policy" "irsa" {
  name   = "autoreparos-${local.servico}-irsa"
  role   = aws_iam_role.irsa.id
  policy = templatefile("${path.module}/politicas/irsa.json.tftpl", { conta = local.conta, regiao = var.regiao })
}
