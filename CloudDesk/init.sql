CREATE TABLE IF NOT EXISTS tickets (
    id SERIAL PRIMARY KEY,
    title VARCHAR(120) NOT NULL,
    description TEXT NOT NULL,
    priority INT NOT NULL CHECK (priority BETWEEN 1 AND 3),
    status VARCHAR(20) NOT NULL DEFAULT 'new' CHECK (status IN ('new', 'in_progress', 'done')),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS ticket_events (
    id SERIAL PRIMARY KEY,
    ticket_id INT NOT NULL REFERENCES tickets(id) ON DELETE CASCADE,
    old_status VARCHAR(20),
    new_status VARCHAR(20) NOT NULL,
    changed_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

INSERT INTO tickets (title, description, priority, status, created_at) VALUES
('Не работает корпоративный VPN', 'При подключении возникает ошибка аутентификации 809.', 1, 'new', NOW() - INTERVAL '5 hours'),
('Замена картриджа в принтере', 'В бухгалтерии закончился тонер в HP LaserJet.', 3, 'done', NOW() - INTERVAL '2 days'),
('Установка Docker Desktop', 'Необходимо установить и настроить права для нового разработчика.', 2, 'in_progress', NOW() - INTERVAL '1 day'),
('Медленно грузится 1С', 'После утреннего обновления наблюдаются задержки при проведении документов.', 1, 'in_progress', NOW() - INTERVAL '3 hours'),
('Настройка переадресации почты', 'Настроить пересылку писем на время отпуска сотрудника.', 3, 'new', NOW() - INTERVAL '30 minutes');
