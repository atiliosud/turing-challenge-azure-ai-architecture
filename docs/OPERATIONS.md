# Operación, calidad y rollout

**Synthetic monitoring:** Azure Function programada envía una parcela fija al API por APIM, espera `completed`, valida geometría/tolerancia + versión de modelo y calcula latencia. Tras N fallos consecutivos envía alerta al Action Group; Logic App abre/actualiza incidencia Jira de forma idempotente. Cerrar automáticamente solo bajo política acordada.

**Alertas:** profundidad/edad de cola, DLQ, fallos del preprocessing y proveedores, p95 E2E, throttling de APIM, errores HTTP, gasto diario de GPU, CPU/IO/locks PostGIS y desajuste resultado esperado. Application Insights + Log Analytics con correlationId, jobId y tenantId pseudonimizado.

**Pruebas:** unitarias de reglas, integración con PostGIS real, contratos de APIM, integración con Service Bus, carga (50 RPS en ráfagas realistas), soak tests, caos (Google/GPU fuera), E2E sintético y dataset etiquetado con umbrales de precisión. Test de idempotencia y duplicación obligatorios.

**Entrega:** build reproducible, unit/integration tests, SCA + SAST + secret scan, SBOM, Bicep lint/what-if, artefactos firmados, despliegue de staging, smoke tests, approvals, canary/blue-green y rollback. OIDC workload identity federation, nunca secrets en YAML.

**Roadmap preliminar:** semanas 1-2 descubrimiento + benchmark modelo/datos; 3-4 MVP E2E con contratos y observabilidad; 5-7 hardening, seguridad, pruebas, facturación y escalado; 8-10 piloto/producción controlada. Un equipo de referencia: 1 arquitecto/technical PM, 1 platform/DevOps, 2 backend, 1 ML engineer compartido, 1 QA compartido; no es compromiso hasta discovery.

**No incluido en el código de ejemplo:** UI, entrenamiento del modelo, conectores externos, base PostGIS, registro transaccional, endpoints GPU y resultado persistido, monitor sintético, Jira, integración de redes privadas y despliegue completo. Estos son elementos del diseño, no capacidades implementadas en esta muestra.
