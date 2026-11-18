cd /workspaces/cloud-lab-01

cat << 'EOF' > README.md
# CloudDesk — Сервис учёта заявок технической поддержки

Веб-сервис на базе ASP.NET Core Minimal API и PostgreSQL, развёртываемый с помощью Docker Compose.

## 1. Запуск приложения

1. Клонируйте репозиторий и перейдите в папку проекта `CloudDesk`:
   ```bash
   cd CloudDesk
   ```
2. Создайте локальный файл конфигурации `.env` на основе шаблона `.env.example` (при необходимости измените пароль):
   ```bash
   cp .env.example .env
   ```
3. Соберите образы и запустите сервисы в фоновом режиме:
   ```bash
   docker compose up -d --build
   ```
4. Убедитесь, что контейнеры базы данных и API запущены и здоровы:
   ```bash
   docker compose ps
   ```

## 2. Список HTTP-маршрутов

* `GET /health` — проверка доступности API и подключения к PostgreSQL (`200 OK` или `503 Service Unavailable`).
* `GET /api/tickets` — получение списка заявок с поддержкой фильтрации, поиска, сортировки и пагинации:
  * `status` — фильтр по статусу (`new`, `in_progress`, `done`)
  * `priority` — фильтр по приоритету (`1`, `2`, `3`)
  * `q` — поиск по подстроке в заголовке (`title`)
  * `sort` — направление сортировки по дате создания (`asc` или `desc`, по умолчанию `desc`)
  * `page` — номер страницы (по умолчанию `1`)
  * `pageSize` — количество элементов на странице (по умолчанию `10`)
* `GET /api/tickets/{id}` — получение одной заявки по идентификатору.
* `POST /api/tickets` — создание новой заявки (JSON: `title`, `description`, `priority`, `status`).
* `PUT /api/tickets/{id}` — обновление заявки; при изменении статуса событие фиксируется в таблице `ticket_events`.
* `DELETE /api/tickets/{id}` — удаление заявки.
* `GET /api/stats` — получение общей статистики и количества заявок по каждому статусу.

## 3. Проверка сохранности данных

Для хранения данных PostgreSQL используется именованный Docker-том `pgdata`.

1. Создайте тестовую заявку через API:
   ```bash
   curl -X POST http://localhost:8080/api/tickets \
     -H "Content-Type: application/json" \
     -d '{"title":"Проверка сохранности","description":"Тест перезапуска контейнеров","priority":2}'
   ```
2. Остановите и удалите контейнеры **без флага `-v`** (чтобы сохранить именованный том `pgdata`):
   ```bash
   docker compose down
   ```
3. Запустите проект повторно:
   ```bash
   docker compose up -d
   ```
4. Проверьте, что созданная заявка осталась в базе данных:
   ```bash
   curl http://localhost:8080/api/tickets
   ```
EOF