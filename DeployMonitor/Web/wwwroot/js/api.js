// API helper with JWT management
const API = {
    getToken() {
        return localStorage.getItem('dm_token');
    },

    setToken(token) {
        localStorage.setItem('dm_token', token);
    },

    getUsername() {
        return localStorage.getItem('dm_username') || '';
    },

    setUsername(username) {
        localStorage.setItem('dm_username', username);
    },

    clearAuth() {
        localStorage.removeItem('dm_token');
        localStorage.removeItem('dm_username');
    },

    isAuthenticated() {
        return !!this.getToken();
    },

    async request(method, url, body) {
        const headers = { 'Content-Type': 'application/json' };
        const token = this.getToken();
        if (token) {
            headers['Authorization'] = 'Bearer ' + token;
        }

        const options = { method, headers };
        if (body) {
            options.body = JSON.stringify(body);
        }

        const response = await fetch(url, options);

        if (response.status === 401) {
            this.clearAuth();
            window.location.href = '/login.html';
            throw new Error('Unauthorized');
        }

        const data = await response.json();
        if (!response.ok) {
            throw new Error(data.error || `HTTP ${response.status}`);
        }
        return data;
    },

    get(url) { return this.request('GET', url); },
    post(url, body) { return this.request('POST', url, body); },
    put(url, body) { return this.request('PUT', url, body); },
    delete(url) { return this.request('DELETE', url); }
};
