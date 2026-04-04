const API_URL = 'http://localhost:5000/api/orders';

async function fetchAndRenderOrders() {
    try {
        // 1. Стучимся на сервер и ждем ответ
        const response = await fetch(API_URL);

        // 2. Превращаем ответ в массив объектов
        const orders = await response.json();

        // 3. Находим наш контейнер на странице
        const container = document.getElementById('eventsContainer');
        
        // Очищаем надпись "Загрузка заказов..."
        container.innerHTML = '';

        // 4. Используем классический цикл 'for' вместо стрелочной функции
        // Мы говорим: начни с нуля (i = 0), и пока 'i' меньше количества заказов, делай шаг вперед (i++)
        for (let i = 0; i < orders.length; i++) {
            
            // Берем конкретный заказ под номером 'i'
            let order = orders[i]; 

            // 5. Классическое условие if-else
            let statusClass;
            
            if (order.StatusText === "Confirmed") {
                statusClass = "status-confirmed"; // Если статус Confirmed, даем зеленый класс
            } else if (order.StatusText === "Pending") {
                statusClass = "status-pending"; // Во всех остальных случаях - красный класс
            } else {
                statusClass = "status-cancelled";
            }
            
            let bigOrderText = '';
            if(order.TotalPrice > 1000){
                bigOrderText = `<p id="btn-big-deal">Крупний заказ!</p>`;
            }

            let dateOption = {
                year: 'numeric',
                month: 'long',
                day: 'numeric'
            }
            let formattedDate = new Date(order.CreatedAt).toLocaleDateString('uk-UA', dateOption);

            const orderHTML = `
                <div class="event-card">
                    <h3>Замовлення #${order.Id}</h3>
                    <p><strong>Дата: </strong>${formattedDate}</p>
                    <p><strong>Кіл-ть:</strong> ${order.Quantity} шт.</p>
                    <p><strong>Сума:</strong> ${order.TotalPrice} грн.</p>
                    <p><strong>Статус:</strong> <span class="${statusClass}">${order.StatusText}</span></p>
                    ${bigOrderText}
                </div>
            `;

            // Добавляем карточку на страницу
            container.innerHTML += orderHTML;
        }

    } catch (error) {
        console.error('Ошибка:', error);
        document.getElementById('eventsContainer').innerHTML = '<p>Не удалось загрузить данные.</p>';
    }
}

fetchAndRenderOrders();