// Адреса сервера твого друга
const API_URL = 'http://localhost:5000/api';

// 1. Функція для отримання та відображення подій
async function loadEvents() {
    const container = document.getElementById('eventsContainer');
    
    try {
        // Робимо GET запит на сервер
        const response = await fetch(`${API_URL}/events`);
        const events = await response.json();
        
        container.innerHTML = ''; // Очищаємо текст "Завантаження..."

        if (events.length === 0) {
            container.innerHTML = '<p>Поки що немає доступних подій.</p>';
            return;
        }

        // Перебираємо кожну подію і створюємо для неї HTML
        events.forEach(event => {
            const card = document.createElement('div');
            card.className = 'event-card';
            // Форматуємо дату (відкидаємо зайве)
            const eventDate = new Date(event.StartDate).toLocaleString();

            card.innerHTML = 
            `
                <h3>${event.Title}</h3>
                <p><b>Опис:</b> ${event.Description}</p>
                <p><b>Дата:</b> ${eventDate}</p>
                <p><b>Ціна:</b> ${event.Price} грн</p>
                <p><b>Залишилось місць:</b> ${event.TotalSeats}</p>
                <div onclick="openModal(${event.Id})" class="btn-buy-ticket">
                    <img src="../images/tickets.svg" alt="Квиток" class="ticket-icon">
                    <a>Купити квиток</a>
                </div>
            `;
            container.appendChild(card);
        });

    } catch (error) {
        console.error('Помилка завантаження подій:', error);
        container.innerHTML = '<p style="color: red;">Помилка зв\'язку з сервером. Переконайтеся, що бекенд запущено.</p>';
    }
}

// 2. Логіка модального вікна
function openModal(eventId) {
    document.getElementById('eventId').value = eventId; // Зберігаємо ID події у приховане поле
    document.getElementById('orderModal').style.display = 'block';
}

function closeModal() {
    document.getElementById('orderModal').style.display = 'none';
    document.getElementById('orderForm').reset(); // Очищаємо форму
}

// 3. Відправка замовлення на сервер
document.getElementById('orderForm').addEventListener('submit', async function(e) {
    e.preventDefault(); // Зупиняємо стандартне перезавантаження сторінки

    const eventId = document.getElementById('eventId').value;
    const email = document.getElementById('clientEmail').value;
    const quantity = document.getElementById('quantity').value;

    // Збираємо дані у форматі, який чекає C# бекенд
    const orderData = {
        EventId: parseInt(eventId),
        Quantity: parseInt(quantity),
        ClientEmail: email
    };

    try {
        // Робимо POST запит
        const response = await fetch(`${API_URL}/orders`, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify(orderData)
        });

        if (response.ok) {
            alert('Замовлення успішно оформлено! Очікуйте підтвердження на вашу пошту.');
            closeModal();
            loadEvents(); // Оновлюємо список подій, щоб кількість місць зменшилась
        } else if (response.status === 409) {
            alert('Помилка: Недостатньо вільних місць!');
        } else {
            alert('Сталася помилка при оформленні замовлення.');
        }

    } catch (error) {
        console.error('Помилка відправки замовлення:', error);
        alert('Помилка зв\'язку з сервером.');
    }
});

// Запускаємо завантаження подій при відкритті сторінки
loadEvents();