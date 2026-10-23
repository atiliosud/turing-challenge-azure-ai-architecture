# Decisiones de arquitectura

Formato corto: contexto, decisión, alternativa descartada y consecuencia.

| ID | Decisión | Descartado y por qué | Consecuencia |
|---|---|---|---|
| ADR-01 | API asíncrona: `202` + `jobId`, polling o webhook | HTTP síncrono: 750 conexiones de 15 s en pico, timeouts en cascada | El cliente integra polling/webhook; la admisión queda en < 500 ms |
| ADR-02 | PostgreSQL Flexible + PostGIS (GiST) para catastro, jobs, outbox y ledger | Cosmos DB: consultas espaciales sobre polígonos limitadas y caras en RU. Azure SQL: válido, menos completo y más caro | Benchmark de 150 M filas en semana 1; el ledger se separa si crece |
| ADR-03 | Inferencia en AKS con pool T4 on-demand + pool Spot, KEDA por cola | ACA serverless GPU: cuota baja para el pico. Azure ML Online Endpoint: otra plataforma sin ganar capacidad | Hay que operar un clúster pequeño; a cambio, batching, Spot y cuota predecible |
| ADR-04 | Outbox transaccional + `jobId` determinista (tenant + `Idempotency-Key`) | Publicar en la cola dentro de la petición: jobs huérfanos o duplicados | El relay del outbox es un componente más (en el repo, el envío directo es solo de referencia) |
| ADR-05 | Resultados en Blob, estado y facturación en PostgreSQL | Blob como índice de estado: sin consultas ni transacciones | Dos almacenes, cada uno para lo que sabe hacer |
| ADR-06 | APIM Standard v2: producto por tier, suscripción por cliente, tenant desde el JWT | Premium: ~4× el coste, solo necesario con multi-región activa | La cuota APIM protege; la factura sale del ledger |
| ADR-07 | Service Bus Premium, una cola por etapa | Storage Queues: sin deduplicación ni DLQ nativa. Standard: sin private endpoints | ~625 €/mes fijo, justificado por red privada y aislamiento |
| ADR-08 | Sonda E2E como Container Apps Job (cron) que entra por Front Door | Azure Function: válida, pero otro runtime y otro pipeline. Disponibilidad HTTP simple: no valida el resultado | La sonda reutiliza imagen, identidad y despliegue |
| ADR-09 | Front Door Premium + WAF como única entrada | DDoS Network Protection: ~2,7 k€/mes, innecesario sin IPs públicas propias | APIM solo acepta la cabecera `X-Azure-FDID` de nuestro perfil |

## Requisitos no funcionales a negociar

- Admisión: p95 < 500 ms; disponibilidad 99,9 % mensual.
- Resultado: p95 ≤ 60 s fuera de pico; en pico, plazo pactado según duración de la ráfaga.
- RPO 15 min, RTO 4 h.
- Sin endpoints públicos en datos; Entra ID y Managed Identity en todo.

## Premisas que hay que validar

- 2 M/año y 1–2 RPS continuos no cuadran (1 RPS todo el año = 31,5 M). Interpreto ráfagas.
- 50 RPS sostenidos consumen los 2 M en 11,1 h: hay que conocer la duración de las ráfagas.
- Throughput real del modelo por GPU y si admite batch.
- Licencia de Google Maps sobre caché y almacenamiento de imágenes.
- Región: el uso de *sqft* sugiere datos de EE. UU.
