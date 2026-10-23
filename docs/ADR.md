# Decisiones de arquitectura (resumen)

| ID | Decisión | Alternativa / coste asociado |
|---|---|---|
| ADR-001 | API asíncrona con `202` y estado por `jobId` | HTTP síncrono de ~15s agravaría timeouts y consumo de conexiones. |
| ADR-002 | PostgreSQL Flexible Server + PostGIS e índice GiST | Cosmos DB no sustituye directamente las operaciones espaciales complejas requeridas. Medir selectividad y planes de consulta. |
| ADR-003 | Worker GPU desacoplado con capacidad controlada por backlog | Azure ML Managed Online Endpoint como alternativa si pesa más el ciclo de vida de ML. |
| ADR-004 | Registro transaccional de jobs + outbox antes de publicar | El API de ejemplo no implementa el outbox; no usar tal cual en producción. |
| ADR-005 | Resultados en Blob y metadatos en una BD operativa | No usar Blob como único índice de estado y facturación. |
| ADR-006 | APIM para autenticación, límite de tasa y cuotas | Las cuotas de APIM no son un ledger financiero exacto; registrar consumo facturable de forma idempotente. |

## Secuencia operativa objetivo

1. APIM valida JWT, tenant, cuota y límite de tasa.
2. API comprueba clave de idempotencia, crea job + evento outbox en transacción y devuelve 202.
3. Relay publica en Service Bus; preprocessing calcula parcela y obtiene imágenes externas.
4. El resultado del preprocessing se persiste; se agenda la inferencia GPU sin repetir llamadas externas innecesariamente.
5. Inferencia guarda artefactos y versión de modelo; postproceso calcula geometrías y reglas de negocio.
6. Estado pasa a `completed`/`failed`; resultado recuperable bajo identidad del mismo tenant.
7. Trazas y métricas registran fases, fallos, coste estimado y latencia; DLQ y reconciliación evitan pérdida silenciosa.

## NFR propuestos para negociar

- Admisión p95 < 500 ms, excluyendo WAN del cliente, en condiciones nominales.
- Objetivo de disponibilidad de admisión 99,9% mensual, sujeto a acuerdo comercial.
- E2E p95 objetivo 30-60 s **fuera de ráfagas**; no prometerlo para 50 RPS hasta probar GPUs.
- RPO <= 15 min y RTO <= 2 h como objetivos iniciales de recuperación.
- Cifrado en tránsito y reposo, redes privadas en dependencias y acceso por identidad administrada.

## Riesgos y validaciones

- 2M/año y 1-2 RPS sostenidos son incoherentes: 1 RPS permanente supone 31,5M/año.
- 50 RPS durante ~11,1 horas consume por sí solo 2M peticiones. Definir duración de ráfagas.
- Inferencia de 10 s a 50 RPS exige 500 slots paralelos si el modelo solo procesa una inferencia concurrente por slot; medir batch y throughput por GPU.
- Google Maps: límites, licencias, costes por llamada, almacenamiento y políticas de uso de imágenes.
- 150M de polígonos: definir volumen en GB, SRID, índices, frecuencia de actualización y consultas reales.
- Egress, retención, private networking y disponibilidad regional GPU afectan coste y SLA.
