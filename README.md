# 🎟️ EventTickets System

Система продажу квитків на події з інтеграцією **Telegram Bot** та **Email-сповіщеннями**. Проект реалізований як backend-сервіс на .NET 8.

## 🚀 Основні можливості
* **REST API:** Керування подіями та замовленнями.
* **PostgreSQL:** Надійна база даних для зберігання замовлень та івентів.
* **Telegram Admin Bot:** Адмін-панель для підтвердження або скасування замовлень у реальному часі.
* **Email Engine:** Автоматична розсилка HTML-квитків через Gmail SMTP.
* **CORS Support:** Готовність до підключення будь-якого Frontend-фреймворка.

## 🛠️ Технологічний стек
* **Мова:** C# (.NET 8)
* **БД:** PostgreSQL + Entity Framework Core
* **API:** HttpListener (Custom Fast Web Server)
* **Бот:** Telegram.Bot Library
* **Парсинг:** AngleSharp / Playwright (для розширення бази подій)

## 📋 API Документація

### Події (Events)
- `GET /api/events` — Отримати всі події. Підтримує фільтрацію за `category`, `price` та `title`.
- `POST /api/events` — Додавання нової події (Admin only).

### Замовлення (Orders)
- `POST /api/orders` — Створення нового замовлення. Очікує `EventId`, `Quantity` та `ClientEmail`.
- `GET /api/orders/{id}` — Отримання статусу замовлення.

## ⚙️ Встановлення та запуск

1. **Налаштуйте змінні середовища (.env):**
   ```env
   TELEGRAM_TOKEN=ваш_токен
   ADMIN_ID=ваш_id
   GMAIL_USER=ваша_пошта
   GMAIL_APP_CODE=код_додатка_gmail
