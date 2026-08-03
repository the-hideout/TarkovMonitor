window.floatingTimerPanel = (() => {
    const initializedElements = new WeakSet();

    function clamp(element, left, top) {
        const maximumLeft = Math.max(0, window.innerWidth - element.offsetWidth);
        const maximumTop = Math.max(0, window.innerHeight - element.offsetHeight);

        return {
            left: Math.min(Math.max(0, left), maximumLeft),
            top: Math.min(Math.max(0, top), maximumTop)
        };
    }

    function applyPosition(element, position) {
        const bounded = clamp(element, position.left, position.top);
        element.style.left = `${bounded.left}px`;
        element.style.top = `${bounded.top}px`;
        element.style.right = "auto";
        element.style.bottom = "auto";
    }

    function initialize(elementId) {
        const element = document.getElementById(elementId);
        if (!element) return;

        if (initializedElements.has(element)) return;
        initializedElements.add(element);

        const handle = element.querySelector(".floating-timer-panel__handle");
        if (!handle) return;

        handle.addEventListener("pointerdown", event => {
            if (event.target.closest("button")) return;

            const rectangle = element.getBoundingClientRect();
            const offsetX = event.clientX - rectangle.left;
            const offsetY = event.clientY - rectangle.top;
            handle.setPointerCapture(event.pointerId);

            const move = moveEvent => {
                applyPosition(element, {
                    left: moveEvent.clientX - offsetX,
                    top: moveEvent.clientY - offsetY
                });
            };

            const finish = finishEvent => {
                handle.releasePointerCapture(finishEvent.pointerId);
                handle.removeEventListener("pointermove", move);
                handle.removeEventListener("pointerup", finish);
                handle.removeEventListener("pointercancel", finish);

            };

            handle.addEventListener("pointermove", move);
            handle.addEventListener("pointerup", finish);
            handle.addEventListener("pointercancel", finish);
        });
    }

    function reset(elementId) {
        const element = document.getElementById(elementId);
        if (!element) return;

        element.style.left = "auto";
        element.style.top = "auto";
        element.style.right = "20px";
        element.style.bottom = "20px";
    }

    return { initialize, reset };
})();
