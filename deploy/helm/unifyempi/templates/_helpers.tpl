{{- define "unifyempi.name" -}}
unifyempi
{{- end }}

{{- define "unifyempi.labels" -}}
app.kubernetes.io/name: {{ include "unifyempi.name" . }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}
