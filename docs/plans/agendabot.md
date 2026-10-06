# agendabot

## Problema
En RD muchas barberías, salones y consultorios agendan citas a mano por WhatsApp: el dueño
contesta mensajes todo el día, se le cruzan horarios y la gente no llega porque nadie le recordó.
AgendaBot es un backend en ASP.NET Core donde un agente con LLM atiende ese WhatsApp: entiende
"¿tienes algo mañana después de las 4 para un corte?", consulta disponibilidad, propone, pide
confirmación, agenda y manda un recordatorio el día anterior.

Para el portfolio, el objetivo es tener un backend .NET que se pueda mostrar en una entrevista de
Backend Junior: Web API, EF Core, SQL Server, auth, concurrencia, background jobs, tests reales,
CI y deploy en vivo.

## Stack
- .NET 10 (LTS), ASP.NET Core Web API con minimal APIs agrupadas por feature
- EF Core 10 + SQL Server (Azure SQL free tier en producción, contenedor en local y en los tests)
- `Microsoft.Extensions.AI` (`IChatClient`) con Claude Haiku 4.5 detrás. El agente no depende del
  proveedor y en los tests se cambia por un cliente falso.
- WhatsApp Cloud API de Meta (número de prueba)
- xUnit + `WebApplicationFactory` + Testcontainers (SQL Server real, no InMemory)
- Serilog, ProblemDetails, health checks y el rate limiter nativo de ASP.NET Core
- GitHub Actions; deploy en Azure App Service (F1) con Docker

## Decisiones
- **Dos proyectos, no cuatro:** `AgendaBot.Api` (carpetas por feature: `Appointments/`,
  `Availability/`, `Agent/`, `WhatsApp/`, `Admin/`) y `AgendaBot.Tests`. Uso el `DbContext`
  directo, sin repositorio genérico encima, porque EF Core ya es unit of work + repository. Es una
  decisión a propósito que se puede defender en una entrevista. Si el proyecto crece, separar un
  `Core` es mover carpetas.
- **Doble reserva:** el chequeo de solapamiento y el insert van en la misma transacción
  `Serializable`, así SQL Server bloquea el rango y la segunda petición concurrente falla. Hay un
  test que lanza dos reservas en paralelo contra SQL Server real y verifica que entra una sola.
- **El modelo no escribe en la base por su cuenta:** las tools de escritura
  (`propose_appointment`, `propose_cancel`) solo guardan una propuesta pendiente en la
  conversación. `confirm_pending` ejecuta esa propuesta exacta, y solo si el último mensaje del
  cliente es una confirmación. La regla vive en C#, no en el prompt.
- **Conversaciones en SQL Server:** tabla `Conversations` + `Messages`. No hace falta una segunda
  base para esto.
- **Identidad del cliente:** el número de WhatsApp (E.164). El primer mensaje crea el `Customer`
  y le pide el nombre. El dueño del negocio entra al panel con JWT.
- **Fechas relativas:** el system prompt lleva fecha y hora actual en `America/Santo_Domingo`. Las
  tools reciben fechas ISO y validan que no estén en el pasado ni fuera del horario.
- **Costo del LLM:** rate limit por número en el webhook y por IP en el chat de demo, tope de
  iteraciones de tools por mensaje y ventana de historial corta. Un chat público sin límite es una
  factura abierta.

## Modelo de datos
Un solo negocio por instancia, así que nombre y zona horaria van en configuración (`Business`),
no en una tabla. `Service` (nombre, duración, precio), `Staff`, `WorkingHours` (por staff y día),
`TimeOff`, `Customer` (teléfono, nombre), `Appointment` (staff, servicio, cliente, inicio, fin,
estado: Pending/Confirmed/Cancelled/NoShow), `Conversation`, `Message`, `PendingAction`.

## Plan
Cada paso es su propia rama y su propio PR.

1. **Base:** solución, proyecto API + tests, Docker Compose con SQL Server, EF Core con la
   primera migración, health check, Serilog, ProblemDetails y CI corriendo `dotnet test`.
2. **Agenda sin IA:**
   - CRUD admin de servicios, staff y horarios (JWT).
   - `GET /availability?serviceId&date` que calcula slots según horario, duración del servicio,
     citas existentes y TimeOff.
   - `POST /appointments` con la transacción serializable.
   - Tests de integración, incluido el de concurrencia.
3. **Agente:**
   - Tools: `list_services`, `get_availability`, `my_appointments`, `propose_appointment`,
     `propose_cancel`, `confirm_pending`.
   - Loop con `IChatClient`, persistencia de la conversación y tope de iteraciones.
   - Tests con un `IChatClient` falso que devuelve tool calls guionadas, por ejemplo para
     verificar que no se crea ninguna cita sin confirmación.
4. **WhatsApp:**
   - `GET /webhook` para el challenge.
   - `POST /webhook`: valida `X-Hub-Signature-256`, deduplica por id de mensaje (índice único),
     responde 200 al instante y procesa en una cola en memoria (`Channel<T>` + `BackgroundService`).
   - Cliente HTTP tipado para enviar mensajes.
5. **Recordatorios:** un `BackgroundService` que cada 15 min busca citas de mañana sin recordar y
   manda una plantilla aprobada de WhatsApp. Si el cliente responde "cancelar", el agente lo
   procesa.
6. **Demo pública:** página de chat simple (HTML estático servido por la API) que habla con el
   mismo agente sin pasar por WhatsApp, con un negocio de ejemplo sembrado y rate limit. Es lo que
   va a probar un reclutador.
7. **Deploy y evals:**
   - Azure App Service + Azure SQL, con secretos en la configuración de App Service.
   - Eval de ~15 conversaciones guionadas que corre contra el modelo real a mano, no en CI: fechas
     relativas, horario lleno, servicio inexistente, cambio de opinión a mitad, "sí" ambiguo.
8. **README como caso de estudio:**
   - Diagrama del flujo.
   - Las decisiones de arriba explicadas.
   - Video de 60 s.
   - Entrada en el portfolio.

## Fuera de alcance por ahora
Pagos, multi-negocio (multi-tenant), audios/imágenes, app móvil del dueño.

## Preguntas abiertas
- Nombre: "AgendaBot" es provisional.
- Cuentas a nombre de Kelvis: Meta for Developers (app de WhatsApp), API key de Anthropic y
  suscripción de Azure (la oferta gratis de Azure SQL pide tarjeta para verificar).
- Verificar en el paso 3 que el SDK de Anthropic para C# expone `IChatClient`. Si no, se usa
  el adaptador de `Microsoft.Extensions.AI` o un `IChatClient` propio delgado sobre su HTTP API.

## Status
in-progress. Pasos 1 a 6 hechos y con tests (22 pasan, en ramas `feature/*` encadenadas).
Paso 7: Dockerfile y evals escritos; las 13 evals pasan contra `gemini-flash-lite-latest`
con una key real (corridas el 2026-10-05, ~6 min). Falta el deploy en Azure (necesita la
cuenta de Kelvis). Paso 8: README hecho; falta video y entrada en el
portfolio.
