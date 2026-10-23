# TechSolutions | Azure B2B Computer Vision — propuesta técnica

**Turing Challenge · 23 octubre 2026 · Atilio Camargo Moreira**

Repositorio de acompañamiento a la propuesta PDF. Su objetivo es hacer visibles las decisiones y los primeros contratos de implementación; **no** es una plataforma desplegable en producción ni una prueba de rendimiento GPU.

## Arquitectura objetivo

`Cliente B2B -> Front Door/WAF -> APIM -> .NET API -> job/outbox -> Service Bus -> preprocessing/PostGIS/Google -> inferencia GPU -> postprocesado -> Blob + metadata -> GET status/result`

Servicios transversales: Entra ID, Managed Identity, Key Vault, Private Endpoints, Application Insights/Log Analytics, Azure Monitor + sintético + Jira.

## Estructura

- `src/TechSolutions.Api`: API .NET 10; contrato POST/GET, demo local y ejemplo de publicación en Service Bus.
- `src/TechSolutions.Worker`: consumidor de mensajes de referencia; **envía a DLQ** porque el pipeline de IA aún no está implementado.
- `infra/main.bicep`: infraestructura parcial (Storage, Service Bus, Log Analytics), con public network deshabilitada.
- `.azuredevops/azure-pipelines.yml`: compilación y puntos pendientes de security/deployment gates.
- `apim/inbound-policy.xml`: JWT, rate limiting, cuota aproximada por suscripción y correlación.
- `docs/ADR.md`: decisiones, NFR y preguntas de dimensionamiento.
- `docs/OPERATIONS.md`: operaciones, pruebas, Jira, delivery y límites de la muestra.

## Ejecutar la API en modo demostración

Con .NET 10 SDK:

```bash
cd src/TechSolutions.Api
dotnet run --environment Development
```

`appsettings.Development.json` activa `DemoMode`. Ejemplo:

```bash
curl -X POST http://localhost:5000/v1/evaluations -H 'Content-Type: application/json' -d '{"address":"Calle Mayor 1, Madrid","idempotencyKey":"demo-001"}'
```

Ajustar puerto al mostrado por `dotnet run`. El endpoint devuelve `202` y un `jobId`; la consulta GET devuelve solo el estado `accepted` almacenado **en memoria**. La demo no realiza inferencia real. En producción la API debe tener autenticación, estado durable, idempotencia y outbox transaccional.

## Infraestructura

```bash
az deployment group what-if --resource-group <rg> --template-file infra/main.bicep --parameters suffix=<unique> location=westeurope
```

**Advertencia:** el Bicep configura acceso público denegado para Storage y Service Bus y por tanto necesita VNet, Private Endpoints/DNS y roles para poder operar. Estos recursos no están en esta plantilla parcial. No desplegar en producción sin ampliar el baseline.

## Hipótesis de capacidad

- 2 segundos de preprocesado + 10 de inferencia + 3 de postproceso = 15 s de tiempo nominal por job.
- A 50 RPS, Little da 750 solicitudes simultáneas equivalentes (no 750 GPUs).
- Si la inferencia procesa 1 job/10 s por slot, 50 RPS requeriría ~500 slots de inferencia; batching y benchmark determinan las GPUs reales.
- 2M peticiones/año son ~0,063 RPS de media, incompatibles con un valle **continuo** de 1-2 RPS. Debe aclararse con el cliente antes de contratar SLA o reservar GPU.
- 2M pares resultado/imagen suponen ~0,81-2,06 TB brutos, sin réplicas ni retención de logs.

## Límites explícitos

Este repositorio es una **referencia de arquitectura**, no una implementación terminada. No incluye la inferencia del modelo ni conexiones Google/catastro reales. Los controles productivos (segregación tenant a tenant, ledger de facturación, backpressure, network baseline, tests automáticos, despliegue y rotación de secretos) están documentados y pendientes de implementar. La estimación de costes requiere benchmark y precios regionales vigentes.
