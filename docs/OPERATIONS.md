# Operación, calidad y despliegue

## Sonda E2E (`src/TechSolutions.SyntheticMonitor`)

- Container Apps Job, cron `*/5 * * * *`, identidad gestionada propia (`id-probe`) con el rol `TechSolutions.Submit` (sin secreto de cliente).
- Entra por Front Door con una suscripción del producto `synthetic` (excluido de facturación).
- Envía la parcela "golden", hace polling hasta el estado final y compara con tolerancia: área ±2 %, nº de polígonos, flags y versión de modelo.
- Dos intentos. Si ambos fallan (resultado incorrecto, error o timeout), crea la incidencia en Jira o comenta en la abierta con la etiqueta `synthetic-e2e`.
- Sale con código 1: la ejecución fallida dispara la alerta de Azure Monitor aunque Jira no responda.
- Secretos (`jira-user`, `jira-api-token`, `probe-subscription-key`) como referencias a Key Vault.

## Alertas (`infra/modules/alerts.bicep`)

| Alerta | Condición | Severidad |
|---|---|---|
| Sonda E2E fallida | cualquier fallo en 15 min | 1 |
| Sonda sin ejecuciones | 20 min sin resultado | 2 |
| Dead-letter | > 0 mensajes en cualquier cola | 2 |
| Backlog de inferencia | > 5.000 mensajes durante 15 min | 2 |
| API 5xx | > 2 % con ≥ 50 peticiones en 5 min | 2 |
| Latencia de admisión | p95 > 500 ms durante 10 min | 3 |
| PostgreSQL CPU | > 80 % durante 15 min | 3 |
| Presupuesto | 70 % y 90 % real, 100 % previsto | 3 |

## Informe mensual

`infra/monthly-report.kql`: peticiones, completadas, fallidas, 4xx/5xx, p95 de admisión, tiempo medio y máximo extremo a extremo y por etapa, por cliente. Se publica como Workbook y se exporta cada mes a `exports/` en Blob. Log Analytics retiene 90 días.

## Pruebas

- Unitarias e integración en CI (23 tests hoy).
- Integración con PostGIS real y Service Bus en el entorno de dev.
- Carga antes de cada temporada: 1, 2 y 50 RPS en ráfagas de 5, 10 y 30 min; soak de 24 h.
- Caos: Google con timeout, nodo GPU desalojado, failover de PostgreSQL, reintentos de Service Bus.
- Restauración de backup al menos una vez por trimestre.
- Modelo: dataset de referencia etiquetado con umbral de calidad antes de promover una versión.

## Despliegue (`.azuredevops/`)

- CI: build, tests con cobertura, gate de paquetes vulnerables, gitleaks, SBOM CycloneDX, lint de Bicep.
- Bootstrap (cada ejecución, idempotente): red, monitorización y datos con el ACR, para que el registro exista antes de subir imágenes.
- Dev: imágenes con `az acr build`, what-if, despliegue, smoke test con la sonda.
- Prod: aprobación en el environment, importación de imágenes desde dev (sin recompilar), canary del 10 %, gate de 5xx, promoción y sonda.
- Workload Identity Federation en las service connections: sin secretos en el pipeline.

## Temporada alta (marzo–mayo)

1. Enero: solicitud de cuota GPU y prueba de carga.
2. Febrero: `gpuMinNodes = 2` y PostgreSQL `Standard_D8ds_v5` en `main.prod.bicepparam`, por pull request.
3. Junio: vuelta a los valores de valle.

## Plan de entrega

Semanas 1–2 discovery y benchmark; 3–5 MVP extremo a extremo; 6–8 hardening; 9–10 producción controlada. Equipo: 1 arquitecto/technical PM, 2 backend .NET, 1 platform/DevOps, 1 ML engineer, QA compartido.
