// Dashboard logic
(function() {
    if (!API.isAuthenticated()) {
        window.location.href = '/login.html';
        return;
    }

    // Display username
    const userEl = document.getElementById('currentUser');
    if (userEl) userEl.textContent = API.getUsername();

    let pollTimer = null;
    let isWatching = false;
    let whitelistItems = [];
    let deleteContainerProject = '';
    let deleteContainerName = '';

    // --- Polling ---

    async function pollDashboard() {
        try {
            const data = await API.get('/api/dashboard');
            isWatching = data.isWatching;
            updateWatchStatus(data.isWatching);
            updateMetrics(data.system);
            updateProjects(data.projects);
            updateSettings(data.settings);
        } catch (err) {
            if (err.message === 'Unauthorized') return;
            console.error('Poll error:', err);
        }
    }

    async function pollLogs() {
        try {
            const [watchData, deployData] = await Promise.all([
                API.get('/api/logs/watch?last=100'),
                API.get('/api/logs/deploy?last=100')
            ]);
            updateLogPanel('watchLogContent', watchData.logs);
            updateLogPanel('deployLogContent', deployData.logs);
        } catch (err) {
            if (err.message === 'Unauthorized') return;
        }
    }

    function startPolling() {
        pollDashboard();
        pollLogs();
        pollTimer = setInterval(() => {
            pollDashboard();
            pollLogs();
        }, 5000);
    }

    function stopPolling() {
        if (pollTimer) {
            clearInterval(pollTimer);
            pollTimer = null;
        }
    }

    // --- UI Updates ---

    function updateWatchStatus(watching) {
        const el = document.getElementById('watchStatus');
        const btn = document.getElementById('btnWatch');
        if (watching) {
            el.textContent = '● 감시중';
            el.className = 'badge badge-watching';
            btn.textContent = '■ 감시 중지';
            btn.className = 'btn btn-danger';
        } else {
            el.textContent = '○ 중지됨';
            el.className = 'badge badge-idle';
            btn.textContent = '● 감시 시작';
            btn.className = 'btn btn-primary';
        }
    }

    function updateMetrics(sys) {
        setMetric('cpu', sys.cpu);
        setMetric('mem', sys.mem);
        setMetric('gpu', sys.gpu);
    }

    function setMetric(name, value) {
        const bar = document.getElementById(name + 'Bar');
        const val = document.getElementById(name + 'Value');
        if (value < 0) {
            val.textContent = 'N/A';
            bar.style.width = '0%';
            return;
        }
        const pct = Math.min(100, Math.max(0, Math.round(value)));
        val.textContent = pct + '%';
        bar.style.width = pct + '%';
        bar.className = 'metric-fill' + (pct > 80 ? ' high' : pct > 50 ? ' medium' : '');
    }

    function updateProjects(projects) {
        const tbody = document.getElementById('projectsBody');
        if (!projects || projects.length === 0) {
            tbody.innerHTML = '<tr><td colspan="6" class="empty-message">프로젝트 없음</td></tr>';
            return;
        }
        tbody.innerHTML = projects.map(p => {
            const statusClass = getStatusClass(p.status);
            const statusText = getStatusText(p.status);
            const encodedName = encodeURIComponent(p.name || '');
            return `<tr>
                <td class="project-name">${esc(p.name)}</td>
                <td><span class="status ${statusClass}">${statusText}</span></td>
                <td class="dim">${esc(p.lastCommitDetectedTime || '')}</td>
                <td>${esc(p.lastDeployTime || '')}</td>
                <td class="dim">${esc(p.lastMessage || '')}</td>
                <td class="actions">
                    <button class="btn btn-xs btn-primary" onclick="deployProject('${encodedName}')" ${p.hasDeployBat ? '' : 'disabled'}>배포</button>
                    <button class="btn btn-xs" onclick="showProjectLog('${encodedName}')">로그</button>
                    <button class="btn btn-xs" onclick="showDockerStatus('${encodedName}')">도커</button>
                </td>
            </tr>`;
        }).join('');
    }

    function getStatusClass(status) {
        const map = { 0: 'idle', 1: 'checking', 2: 'deploying', 3: 'success', 4: 'error', 5: 'not-configured' };
        return 'status-' + (map[status] || 'idle');
    }

    function getStatusText(status) {
        const map = { 0: '● 대기', 1: '⟳ 확인중', 2: '⟳ 배포중', 3: '✓ 정상', 4: '✗ 오류', 5: '— 미설정' };
        return map[status] || '?';
    }

    function updateSettings(settings) {
        const repoEl = document.getElementById('settRepoFolder');
        const deployEl = document.getElementById('settDeployFolder');
        const intervalEl = document.getElementById('settInterval');
        const branchEl = document.getElementById('settBranch');
        // Only update if settings panel is not focused
        if (document.activeElement !== repoEl) repoEl.value = settings.repoFolder || '';
        if (document.activeElement !== deployEl) deployEl.value = settings.deployFolder || '';
        if (document.activeElement !== intervalEl) intervalEl.value = settings.intervalSeconds || 30;
        if (document.activeElement !== branchEl) branchEl.value = settings.defaultBranch || 'master';
        // Whitelist: only update if input is not focused
        const wlInput = document.getElementById('settWhitelistInput');
        if (document.activeElement !== wlInput) {
            const raw = settings.globalExitedOkContainers || '';
            const items = raw.split(/[\s,;]+/).filter(Boolean);
            if (JSON.stringify(items) !== JSON.stringify(whitelistItems)) {
                whitelistItems = items;
                renderWhitelistItems();
            }
        }
    }

    function updateLogPanel(elementId, logs) {
        const el = document.getElementById(elementId);
        if (!el) return;
        const wasAtBottom = el.scrollTop + el.clientHeight >= el.scrollHeight - 20;
        el.textContent = (logs || []).join('\n');
        if (wasAtBottom) {
            el.scrollTop = el.scrollHeight;
        }
    }

    function esc(str) {
        const d = document.createElement('div');
        d.textContent = str;
        return d.innerHTML;
    }

    // --- Actions (global scope) ---

    window.toggleWatch = async function() {
        try {
            if (isWatching) {
                await API.post('/api/watch/stop');
            } else {
                await API.post('/api/watch/start');
            }
            await pollDashboard();
        } catch (err) {
            alert('오류: ' + err.message);
        }
    };

    window.refreshProjects = async function() {
        try {
            await API.post('/api/watch/refresh');
            setTimeout(pollDashboard, 1000);
        } catch (err) {
            alert('오류: ' + err.message);
        }
    };

    window.deployProject = async function(encodedName) {
        const name = decodeURIComponent(encodedName);
        if (!confirm(`${name} 프로젝트를 수동 배포하시겠습니까?`)) return;
        try {
            await API.post(`/api/projects/${encodeURIComponent(name)}/deploy`);
            setTimeout(pollDashboard, 1000);
        } catch (err) {
            alert('배포 실패: ' + err.message);
        }
    };

    window.showProjectLog = async function(encodedName) {
        const name = decodeURIComponent(encodedName);
        try {
            const data = await API.get(`/api/projects/${encodeURIComponent(name)}/log`);
            document.getElementById('deployLogTitle').textContent = `[${name}] 배포 로그`;
            document.getElementById('deployLogDetail').textContent = data.log || '로그 없음';
            document.getElementById('deployLogModal').style.display = 'flex';
        } catch (err) {
            alert('로그 조회 실패: ' + err.message);
        }
    };

    window.hideDeployLog = function() {
        document.getElementById('deployLogModal').style.display = 'none';
    };

    // --- Whitelist Management ---

    function renderWhitelistItems() {
        const container = document.getElementById('whitelistItems');
        if (!container) return;
        container.innerHTML = whitelistItems.map((item, i) =>
            `<span class="whitelist-tag">${esc(item)} <button onclick="removeWhitelistItem(${i})">&times;</button></span>`
        ).join('');
    }

    window.addWhitelistItem = function() {
        const input = document.getElementById('settWhitelistInput');
        const text = (input.value || '').trim();
        if (!text) return;
        if (!whitelistItems.some(x => x.toLowerCase() === text.toLowerCase())) {
            whitelistItems.push(text);
            renderWhitelistItems();
        }
        input.value = '';
        input.focus();
    };

    // Enter key support for whitelist input
    document.addEventListener('keydown', function(e) {
        if (e.target && e.target.id === 'settWhitelistInput' && e.key === 'Enter') {
            e.preventDefault();
            window.addWhitelistItem();
        }
    });

    window.removeWhitelistItem = function(index) {
        if (index >= 0 && index < whitelistItems.length) {
            whitelistItems.splice(index, 1);
            renderWhitelistItems();
        }
    };

    // --- Docker Container Delete ---

    window.deleteContainer = function(encodedProject, encodedContainer) {
        deleteContainerProject = decodeURIComponent(encodedProject);
        deleteContainerName = decodeURIComponent(encodedContainer);
        document.getElementById('deleteContainerMsg').textContent =
            `Remove container "${deleteContainerName}"?`;
        document.getElementById('deleteConfirmInput').value = '';
        document.getElementById('deleteConfirmBtn').disabled = true;
        document.getElementById('deleteContainerError').style.display = 'none';
        document.getElementById('deleteContainerModal').style.display = 'flex';
        document.getElementById('deleteConfirmInput').focus();
    };

    window.onDeleteConfirmInput = function() {
        const val = document.getElementById('deleteConfirmInput').value.trim();
        document.getElementById('deleteConfirmBtn').disabled = (val !== 'DELETE');
    };

    window.confirmDeleteContainer = async function() {
        const val = document.getElementById('deleteConfirmInput').value.trim();
        if (val !== 'DELETE') return;

        const errEl = document.getElementById('deleteContainerError');
        errEl.style.display = 'none';

        try {
            await API.delete(
                `/api/projects/${encodeURIComponent(deleteContainerProject)}/containers/${encodeURIComponent(deleteContainerName)}`);
            document.getElementById('deleteContainerModal').style.display = 'none';
            // Refresh docker modal
            window.showDockerStatus(encodeURIComponent(deleteContainerProject));
        } catch (err) {
            errEl.textContent = err.message;
            errEl.style.display = 'block';
        }
    };

    window.cancelDeleteContainer = function() {
        document.getElementById('deleteContainerModal').style.display = 'none';
    };

    // Enter key support for delete confirm input
    document.addEventListener('keydown', function(e) {
        if (e.target && e.target.id === 'deleteConfirmInput' && e.key === 'Enter') {
            e.preventDefault();
            if (e.target.value.trim() === 'DELETE') {
                window.confirmDeleteContainer();
            }
        }
    });

    window.showDockerStatus = async function(encodedName) {
        const name = decodeURIComponent(encodedName);
        const titleEl = document.getElementById('dockerModalTitle');
        const summaryEl = document.getElementById('dockerSummary');
        const bodyEl = document.getElementById('dockerContainersBody');
        const logTitleEl = document.getElementById('dockerLogTitle');
        const logEl = document.getElementById('dockerContainerLog');

        try {
            const data = await API.get(`/api/projects/${encodeURIComponent(name)}/containers`);
            titleEl.textContent = `[${name}] Docker 상태`;
            summaryEl.textContent = data.message || '';
            logTitleEl.textContent = '컨테이너 로그';
            logEl.textContent = '컨테이너를 선택하면 로그를 표시합니다.';

            const list = data.containers || [];
            if (list.length === 0) {
                bodyEl.innerHTML = '<tr><td colspan="5" class="empty-message">매칭된 컨테이너 없음</td></tr>';
            } else {
                bodyEl.innerHTML = list.map(c => {
                    const levelText =
                        c.level === 'running' ? 'running' :
                        c.level === 'expected-stopped' ? 'exited(0)-허용' :
                        c.level === 'error' ? '오류' : '-';
                    const statusClass =
                        c.level === 'running' ? 'status-success' :
                        c.level === 'expected-stopped' ? 'status-idle' :
                        c.level === 'error' ? 'status-error' : 'status-idle';
                    const encodedContainer = encodeURIComponent(c.name || '');
                    const encodedProject = encodeURIComponent(name);
                    const isRunning = c.state === 'running';
                    return `<tr>
                        <td>${esc(c.name || '')}</td>
                        <td><span class="status ${statusClass}">${esc(levelText)}</span></td>
                        <td class="dim">${esc(c.state || '')}</td>
                        <td class="dim">${esc(c.status || '')}</td>
                        <td class="actions">
                            <button class="btn btn-xs" onclick="showContainerLogs('${encodedProject}','${encodedContainer}')">로그</button>
                            <button class="btn btn-xs btn-danger" onclick="deleteContainer('${encodedProject}','${encodedContainer}')" ${isRunning ? 'disabled title="실행 중인 컨테이너는 삭제 불가"' : ''}>삭제</button>
                        </td>
                    </tr>`;
                }).join('');
            }

            document.getElementById('dockerModal').style.display = 'flex';
        } catch (err) {
            alert('Docker 상태 조회 실패: ' + err.message);
        }
    };

    window.showContainerLogs = async function(encodedProject, encodedContainer) {
        const projectName = decodeURIComponent(encodedProject);
        const containerName = decodeURIComponent(encodedContainer);
        const logTitleEl = document.getElementById('dockerLogTitle');
        const logEl = document.getElementById('dockerContainerLog');
        try {
            logTitleEl.textContent = `${containerName} (최근 120줄)`;
            logEl.textContent = '로그 로딩 중...';
            const data = await API.get(
                `/api/projects/${encodeURIComponent(projectName)}/containers/${encodeURIComponent(containerName)}/logs?tail=120`);
            logEl.textContent = data.logs || '로그 없음';
            logEl.scrollTop = 0;
        } catch (err) {
            logEl.textContent = `로그 조회 실패: ${err.message}`;
        }
    };

    window.hideDockerModal = function() {
        document.getElementById('dockerModal').style.display = 'none';
    };

    window.switchLogTab = function(btn) {
        document.querySelectorAll('.log-tab').forEach(t => t.classList.remove('active'));
        document.querySelectorAll('.log-panel').forEach(p => p.classList.remove('active'));
        btn.classList.add('active');
        const panel = document.getElementById(btn.dataset.tab);
        if (panel) panel.classList.add('active');
        // Load history when switching to history tab
        if (btn.dataset.tab === 'history') loadHistory();
    };

    window.toggleSettings = function() {
        const panel = document.getElementById('settingsPanel');
        panel.style.display = panel.style.display === 'none' ? 'block' : 'none';
    };

    window.saveSettings = async function() {
        try {
            await API.put('/api/settings', {
                repoFolder: document.getElementById('settRepoFolder').value,
                deployFolder: document.getElementById('settDeployFolder').value,
                intervalSeconds: parseInt(document.getElementById('settInterval').value) || 30,
                defaultBranch: document.getElementById('settBranch').value,
                globalExitedOkContainers: whitelistItems.join(' ')
            });
            document.getElementById('settingsPanel').style.display = 'none';
            await pollDashboard();
        } catch (err) {
            alert('설정 저장 실패: ' + err.message);
        }
    };

    window.saveAccountSettings = async function() {
        const oldPassword = document.getElementById('settAccountOldPassword').value;
        const newUsername = document.getElementById('settAccountUsername').value.trim();
        const newPassword = document.getElementById('settAccountNewPassword').value;
        const errEl = document.getElementById('settAccountError');

        errEl.style.display = 'none';
        errEl.textContent = '';

        if (!oldPassword) {
            errEl.textContent = '현재 비밀번호를 입력하세요.';
            errEl.style.display = 'block';
            return;
        }
        if (!newUsername && !newPassword) {
            errEl.textContent = '새 아이디 또는 새 비밀번호를 입력하세요.';
            errEl.style.display = 'block';
            return;
        }

        try {
            const data = await API.post('/api/auth/change-credentials', {
                oldPassword,
                newUsername: newUsername || null,
                newPassword: newPassword || null
            });

            if (data.token) API.setToken(data.token);
            if (data.username) {
                API.setUsername(data.username);
                const userEl = document.getElementById('currentUser');
                if (userEl) userEl.textContent = data.username;
            }

            document.getElementById('settAccountOldPassword').value = '';
            document.getElementById('settAccountNewPassword').value = '';
            alert(data.message || '계정 정보가 변경되었습니다.');
        } catch (err) {
            errEl.textContent = err.message;
            errEl.style.display = 'block';
        }
    };

    window.showChangePassword = function() {
        document.getElementById('changePwModal').style.display = 'flex';
        document.getElementById('oldPassword').value = '';
        document.getElementById('newPassword').value = '';
        document.getElementById('changePwError').style.display = 'none';
    };

    window.hideChangePassword = function() {
        document.getElementById('changePwModal').style.display = 'none';
    };

    window.changePassword = async function() {
        const oldPw = document.getElementById('oldPassword').value;
        const newPw = document.getElementById('newPassword').value;
        const errEl = document.getElementById('changePwError');

        if (!oldPw || !newPw) {
            errEl.textContent = '모든 항목을 입력하세요.';
            errEl.style.display = 'block';
            return;
        }

        try {
            await API.post('/api/auth/change-password', {
                oldPassword: oldPw,
                newPassword: newPw
            });
            alert('비밀번호가 변경되었습니다.');
            hideChangePassword();
        } catch (err) {
            errEl.textContent = err.message;
            errEl.style.display = 'block';
        }
    };

    window.logout = function() {
        API.clearAuth();
        window.location.href = '/login.html';
    };

    async function loadHistory() {
        try {
            const data = await API.get('/api/history?limit=50');
            const container = document.getElementById('historyContent');
            if (!data.history || data.history.length === 0) {
                container.innerHTML = '<p class="empty-message">배포 이력 없음</p>';
                return;
            }
            container.innerHTML = '<table class="history-table"><thead><tr>' +
                '<th>프로젝트</th><th>상태</th><th>시작</th><th>소요(초)</th><th>트리거</th><th>커밋</th>' +
                '</tr></thead><tbody>' +
                data.history.map(h => `<tr class="${h.status === 'Success' ? '' : 'error-row'}">
                    <td>${esc(h.project_name || '')}</td>
                    <td>${h.status === 'Success' ? '✓ 성공' : '✗ 실패'}</td>
                    <td>${esc(h.started_at || '')}</td>
                    <td>${h.duration_sec != null ? h.duration_sec.toFixed(1) : '-'}</td>
                    <td>${esc(h.trigger_type || 'auto')}</td>
                    <td class="dim">${esc((h.commit_hash || '').substring(0, 7))}</td>
                </tr>`).join('') +
                '</tbody></table>';
        } catch (err) {
            console.error('History load error:', err);
        }
    }

    // Close modals on backdrop click
    document.querySelectorAll('.modal').forEach(modal => {
        modal.addEventListener('click', (e) => {
            if (e.target === modal) modal.style.display = 'none';
        });
    });

    // Start
    startPolling();
})();
