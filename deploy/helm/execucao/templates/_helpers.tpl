{{/* Nome dos objetos: o nome do serviço (release, Service, ServiceAccount e namespace usam o mesmo nome). */}}
{{- define "servico.nome" -}}
{{- .Chart.Name -}}
{{- end }}

{{- define "servico.seletor" -}}
app.kubernetes.io/name: {{ include "servico.nome" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{- define "servico.rotulos" -}}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version }}
{{ include "servico.seletor" . }}
app.kubernetes.io/version: {{ .Values.image.tag | quote }}
app.kubernetes.io/part-of: autoreparos
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{- define "servico.imagem" -}}
{{ .Values.image.repository }}:{{ .Values.image.tag }}
{{- end }}

{{/* URL de uma fila SQS montada pelo nome (a fila pertence ao consumidor e é criada pelo Terraform dele). */}}
{{- define "servico.fila" -}}
{{- $raiz := index . 0 -}}
https://sqs.{{ $raiz.Values.aws.regiao }}.amazonaws.com/{{ $raiz.Values.aws.conta }}/{{ index . 1 }}
{{- end }}

{{/* Connection string do serviço, lida do Secret <servico>-database criado pelo job deploy. */}}
{{- define "servico.conexao" -}}
- name: {{ .Values.database.variavel }}
  valueFrom:
    secretKeyRef:
      name: {{ include "servico.nome" . }}-database
      key: {{ .Values.database.variavel }}
{{- end }}

{{- define "servico.seguranca" -}}
securityContext:
  runAsNonRoot: true
  runAsUser: 1654
  runAsGroup: 1654
  fsGroup: 1654
  seccompProfile:
    type: RuntimeDefault
{{- end }}

{{- define "servico.segurancaContainer" -}}
securityContext:
  allowPrivilegeEscalation: false
  privileged: false
  runAsNonRoot: true
  runAsUser: 1654
  runAsGroup: 1654
  capabilities:
    drop:
      - ALL
{{- end }}
