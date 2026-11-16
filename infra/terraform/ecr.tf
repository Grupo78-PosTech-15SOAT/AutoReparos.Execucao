resource "aws_ecr_repository" "servico" {
  name = "autoreparos-${local.servico}"
  # A tag é o SHA do commit; mutável para que a reexecução do mesmo commit consiga publicar de novo.
  image_tag_mutability = "MUTABLE"
  force_delete         = true

  image_scanning_configuration {
    scan_on_push = true
  }
}

resource "aws_ecr_lifecycle_policy" "servico" {
  repository = aws_ecr_repository.servico.name
  policy = jsonencode({
    rules = [{
      rulePriority = 1
      description  = "Mantém as 10 imagens mais recentes"
      selection = {
        tagStatus   = "any"
        countType   = "imageCountMoreThan"
        countNumber = 10
      }
      action = { type = "expire" }
    }]
  })
}
