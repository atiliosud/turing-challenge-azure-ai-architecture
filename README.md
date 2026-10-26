# TechSolutions · API B2B de visión artificial en Azure

**Turing Challenge · Prueba técnica de arquitectura · Atilio Camargo Moreira · octubre 2026**

**Documento principal:** [`docs/TechSolutions_Arquitectura_Azure_AtilioCamargo.pdf`](docs/TechSolutions_Arquitectura_Azure_AtilioCamargo.pdf)

Este repositorio acompaña al documento. Es un esqueleto real (compila, tiene tests y la infraestructura valida), no un sistema terminado: el modelo de IA y los conectores a Google y al catastro quedan fuera.

## Arquitectura

![Arquitectura](docs/diagrams/architecture.png)

Los números siguen el flujo de una petición descrito en el documento (sección 3). Fuentes: [`architecture.drawio`](docs/diagrams/architecture.drawio) (editable) y [`architecture.svg`](docs/diagrams/architecture.svg).

## Contenido

| Ruta | Qué hay |
|---|---|
| `src/TechSolutions.Api` | API .NET 10: `POST /v1/evaluations`, `GET` de estado, resultado y uso; `Idempotency-Key` con jobId determinista, aislamiento por tenant, JWT de Entra ID, readiness contra Service Bus y telemetría OpenTelemetry → Application Insights (auth Entra). |
| `src/TechSolutions.Worker` | Worker de referencia por etapa: consume su cola, continúa la traza de la API y emite `stage.duration` y `job.e2e.duration` por tenant (los handlers reales del pipeline no están). |
| `src/TechSolutions.SyntheticMonitor*` | Sonda E2E: envía la parcela "golden", valida con tolerancia y abre/actualiza la incidencia en Jira sin duplicar. |
| `tests/` | 23 tests: contrato, idempotencia y aislamiento de la API, readiness, validación de la sonda y deduplicación en Jira. |
| `infra/` | Bicep modular: red privada, Storage, Service Bus, Key Vault, ACR, PostgreSQL + PostGIS, Container Apps (canary 90/10 en la plantilla), AKS GPU, APIM, Front Door + WAF, alertas y presupuesto. Identidades separadas para apps, escalado KEDA, inferencia y sonda. |
| `k8s/` | Inferencia en AKS: Deployment con workload identity, `ScaledObject` de KEDA y NVIDIA device plugin. |
| `infra/monthly-report.kql` | Informe mensual de tiempos, volumen y errores por cliente. |
| `apim/policies/` | Política de la API (JWT, tenant desde el token, solo tráfico de Front Door), política del `POST` (validación del cuerpo, métrica de uso) y una por producto. |
| `.azuredevops/` | Pipeline multi-stage: CI con gates; dev con bootstrap del ACR e imágenes; prod con aprobación, canary con gate de 5xx y rollback automático. |
| `docs/` | Documento PDF, ADR y operación. |

## Estado del esqueleto

Es una implementación de referencia, no un sistema terminado. Lo que hay que saber antes de desplegarla:

- Los workers no tienen los handlers reales (Google, PostGIS, modelo): **envían cada mensaje a la dead-letter queue a propósito** y registran la métrica del job como `failed`.
- Fuera de `DemoMode` no existe aún el almacén de jobs en PostgreSQL, así que `GET /v1/evaluations/{jobId}` responde `404`.
- Consecuencia: en un despliegue real, el smoke test de dev y la sonda E2E **fallarán hasta que existan los handlers**. Es el comportamiento esperado, no un bug: la sonda está para detectar justo eso.

## Ejecutar en local

```bash
dotnet test TuringChallenge.sln

cd src/TechSolutions.Api
dotnet run      # perfil "demo": Development + DemoMode en http://localhost:5180, estado en memoria, sin Azure

curl -i -X POST http://localhost:5180/v1/evaluations \
  -H 'Content-Type: application/json' -H 'Idempotency-Key: demo-001' \
  -d '{"address":"4512 Maple Ridge Dr, Plano, TX 75093"}'
```

El mismo `Idempotency-Key` devuelve el mismo `jobId`. `GET /v1/evaluations/{jobId}/result` responde `409` mientras el job no termina (en la demo no hay pipeline, así que nunca termina) y `GET /v1/usage` devuelve el consumo del tenant.

## Infraestructura

```bash
az bicep build -f infra/main.bicep
az deployment group what-if -g <rg> -f infra/main.bicep -p infra/main.dev.bicepparam
```

Requisitos previos fuera de la plantilla: app registration de la API que emita tokens v2 (`"requestedAccessTokenVersion": 2` en el manifiesto; la API y APIM leen el claim `azp`) con el rol `TechSolutions.Submit` (asignado también a la identidad de la sonda, salida `probePrincipalId`), grupo de Entra para administrar PostgreSQL, secretos en Key Vault (`jira-user`, `jira-api-token`, `probe-subscription-key`) y cuota de GPU en la región. Los manifiestos de `k8s/` se aplican después del despliegue (ver `k8s/README.md`).

## Números clave

- 2 + 10 + 3 = 15 s por petición; a 50 RPS hay 750 peticiones en vuelo. Por eso la API es asíncrona.
- 2 M peticiones/año = 0,063 RPS de media; los 50 RPS solo pueden ser ráfagas.
- Coste base estimado: ~3,4 k€/mes en valle, ~5,2 k€/mes en pico, ~46 k€/año (Google Maps aparte).
