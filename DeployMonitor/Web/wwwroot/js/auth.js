// Login page logic
(function() {
    // If already authenticated, redirect to dashboard
    if (API.isAuthenticated()) {
        window.location.href = '/';
        return;
    }

    const form = document.getElementById('loginForm');
    const errorEl = document.getElementById('loginError');
    const loginBtn = document.getElementById('loginBtn');

    form.addEventListener('submit', async (e) => {
        e.preventDefault();
        errorEl.style.display = 'none';

        const username = document.getElementById('username').value.trim();
        const password = document.getElementById('password').value;

        if (!username || !password) {
            errorEl.textContent = '사용자명과 비밀번호를 입력하세요.';
            errorEl.style.display = 'block';
            return;
        }

        loginBtn.disabled = true;
        loginBtn.textContent = '로그인 중...';

        try {
            const data = await API.post('/api/auth/login', { username, password });
            API.setToken(data.token);
            API.setUsername(data.username);
            window.location.href = '/';
        } catch (err) {
            errorEl.textContent = err.message || '로그인에 실패했습니다.';
            errorEl.style.display = 'block';
        } finally {
            loginBtn.disabled = false;
            loginBtn.textContent = '로그인';
        }
    });
})();
