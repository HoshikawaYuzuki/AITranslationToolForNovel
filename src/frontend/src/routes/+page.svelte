<script lang="ts">
  import {
    checkHealth,
    getCurrentUser,
    logout,
    uploadNovel,
    getApiKeyStatus,
    setApiKey,
    deleteApiKey,
    getNovels,
    createTranslationJob,
    getTranslationJobs,
    getTranslationJob,
    cancelTranslationJob,
    type HealthResponse,
    type UserResponse,
    type UploadResponse,
    type ApiKeyStatus,
    type NovelSummary,
    type TranslationJobSummary,
    type TranslationJobDetail
  } from '$lib/api/client';

  // Health state
  let health = $state<HealthResponse | null>(null);
  let healthError = $state<string | null>(null);
  let healthLoading = $state(true);

  // Auth state
  let user = $state<UserResponse | null>(null);
  let authLoading = $state(true);
  let authError = $state<string | null>(null);

  // Upload state
  let selectedFile = $state<File | null>(null);
  let novelTitle = $state('');
  let sourceLanguage = $state('ja');
  let uploadLoading = $state(false);
  let uploadError = $state<string | null>(null);
  let uploadSuccess = $state<UploadResponse | null>(null);

  // BYOK API Keys state
  let apiKeyStatus = $state<ApiKeyStatus>({});
  let selectedProvider = $state<'gemini' | 'openai'>('gemini');
  let inputApiKey = $state('');
  let keyLoading = $state(false);
  let keyMessage = $state<string | null>(null);
  let keyError = $state<string | null>(null);

  // Novels & Translation Jobs state
  let novels = $state<NovelSummary[]>([]);
  let selectedNovelId = $state<string>('');
  let targetLanguage = $state('vi');
  let translationProvider = $state<'gemini' | 'openai' | 'mock'>('mock');
  let translationModel = $state('mock-v1');
  let translationLoading = $state(false);
  let translationError = $state<string | null>(null);

  let jobs = $state<TranslationJobSummary[]>([]);
  let activeJobDetail = $state<TranslationJobDetail | null>(null);
  let activeJobId = $state<string | null>(null);
  let jobPollingInterval: any = null;

  let isHealthy = $derived(health?.status === 'healthy');
  let dbConnected = $derived(health?.database === 'connected');

  async function fetchHealth() {
    healthLoading = true;
    healthError = null;
    try {
      health = await checkHealth();
    } catch (e) {
      healthError = e instanceof Error ? e.message : 'Failed to connect to backend';
      health = null;
    } finally {
      healthLoading = false;
    }
  }

  async function fetchUser() {
    authLoading = true;
    authError = null;
    try {
      user = await getCurrentUser();
      if (user) {
        await loadUserData();
      }
    } catch (e) {
      authError = e instanceof Error ? e.message : 'Failed to fetch user session';
      user = null;
    } finally {
      authLoading = false;
    }
  }

  async function loadUserData() {
    await Promise.all([
      fetchApiKeyStatus(),
      fetchNovels(),
      fetchTranslationJobs()
    ]);
  }

  async function fetchApiKeyStatus() {
    try {
      apiKeyStatus = await getApiKeyStatus();
    } catch (e) {
      console.error('Failed to load API key status', e);
    }
  }

  async function fetchNovels() {
    try {
      novels = await getNovels();
      if (novels.length > 0 && !selectedNovelId) {
        selectedNovelId = novels[0].id;
      }
    } catch (e) {
      console.error('Failed to load novels', e);
    }
  }

  async function fetchTranslationJobs() {
    try {
      jobs = await getTranslationJobs();
      if (jobs.length > 0 && !activeJobId) {
        await selectJob(jobs[0].id);
      }
    } catch (e) {
      console.error('Failed to load translation jobs', e);
    }
  }

  async function handleLogout() {
    authLoading = true;
    authError = null;
    try {
      await logout();
      user = null;
      uploadSuccess = null;
      jobs = [];
      novels = [];
      activeJobDetail = null;
      activeJobId = null;
      stopPolling();
    } catch (e) {
      authError = e instanceof Error ? e.message : 'Failed to logout';
    } finally {
      authLoading = false;
    }
  }

  async function handleSaveApiKey(e: SubmitEvent) {
    e.preventDefault();
    if (!inputApiKey.trim()) {
      keyError = 'Please enter a valid API key.';
      return;
    }
    keyLoading = true;
    keyError = null;
    keyMessage = null;
    try {
      await setApiKey(selectedProvider, inputApiKey.trim());
      keyMessage = `API Key for ${selectedProvider.toUpperCase()} stored securely in RAM.`;
      inputApiKey = '';
      await fetchApiKeyStatus();
      if (activeJobId) {
        await refreshActiveJob();
      }
    } catch (e) {
      keyError = e instanceof Error ? e.message : 'Failed to save API key.';
    } finally {
      keyLoading = false;
    }
  }

  async function handleDeleteApiKey(provider: string) {
    keyLoading = true;
    keyError = null;
    keyMessage = null;
    try {
      await deleteApiKey(provider);
      keyMessage = `API Key for ${provider.toUpperCase()} removed from RAM.`;
      await fetchApiKeyStatus();
    } catch (e) {
      keyError = e instanceof Error ? e.message : 'Failed to delete API key.';
    } finally {
      keyLoading = false;
    }
  }

  function handleFileChange(event: Event) {
    const input = event.target as HTMLInputElement;
    if (input.files && input.files[0]) {
      const file = input.files[0];
      if (!file.name.toLowerCase().endsWith('.txt')) {
        uploadError = 'Only .txt files are supported.';
        selectedFile = null;
        return;
      }
      if (file.size > 10 * 1024 * 1024) {
        uploadError = 'File size exceeds the 10 MB limit.';
        selectedFile = null;
        return;
      }
      selectedFile = file;
      uploadError = null;
      if (!novelTitle) {
        novelTitle = file.name.replace(/\.txt$/i, '');
      }
    }
  }

  async function handleUpload(event: SubmitEvent) {
    event.preventDefault();
    if (!selectedFile) {
      uploadError = 'Please select a .txt file to upload.';
      return;
    }
    if (!novelTitle.trim()) {
      uploadError = 'Please enter a novel title.';
      return;
    }

    uploadLoading = true;
    uploadError = null;
    uploadSuccess = null;

    try {
      const result = await uploadNovel(selectedFile, novelTitle.trim(), sourceLanguage);
      uploadSuccess = result;
      selectedFile = null;
      novelTitle = '';
      await fetchNovels();
      selectedNovelId = result.novelId;
    } catch (e) {
      uploadError = e instanceof Error ? e.message : 'Upload failed.';
    } finally {
      uploadLoading = false;
    }
  }

  function onProviderChange(prov: 'gemini' | 'openai' | 'mock') {
    translationProvider = prov;
    if (prov === 'gemini') translationModel = 'gemini-2.5-flash';
    else if (prov === 'openai') translationModel = 'gpt-4o-mini';
    else translationModel = 'mock-v1';
  }

  async function handleStartTranslation(e: SubmitEvent) {
    e.preventDefault();
    if (!selectedNovelId) {
      translationError = 'Please select a novel to translate.';
      return;
    }
    translationLoading = true;
    translationError = null;

    try {
      const job = await createTranslationJob(
        selectedNovelId,
        targetLanguage.trim(),
        translationProvider,
        translationModel.trim()
      );
      await fetchTranslationJobs();
      await selectJob(job.id);
    } catch (e) {
      translationError = e instanceof Error ? e.message : 'Failed to submit translation.';
    } finally {
      translationLoading = false;
    }
  }

  let isRefreshingJob = false;

  async function selectJob(id: string) {
    activeJobId = id;
    await refreshActiveJob();
    startPolling();
  }

  async function refreshActiveJob() {
    if (!activeJobId || isRefreshingJob) return;
    isRefreshingJob = true;
    const targetId = activeJobId;
    try {
      const detail = await getTranslationJob(targetId);
      // F09: Guard against race conditions and out-of-order responses when switching jobs
      if (activeJobId === targetId && detail) {
        activeJobDetail = detail;
        const idx = jobs.findIndex(j => j.id === targetId);
        if (idx !== -1) {
          jobs[idx] = { ...detail };
        }
        if (detail.status === 'Completed' || detail.status === 'Failed' || detail.status === 'Cancelled') {
          stopPolling();
        }
      }
    } catch (e) {
      console.error('Failed to refresh job', e);
    } finally {
      isRefreshingJob = false;
    }
  }

  async function handleCancelJob(id: string) {
    try {
      await cancelTranslationJob(id);
      await refreshActiveJob();
      await fetchTranslationJobs();
    } catch (e) {
      translationError = e instanceof Error ? e.message : 'Failed to cancel job.';
    }
  }

  function startPolling() {
    stopPolling();
    jobPollingInterval = setInterval(() => {
      if (activeJobDetail && (activeJobDetail.status === 'Queued' || activeJobDetail.status === 'Processing' || activeJobDetail.status === 'WaitingForApiKey')) {
        refreshActiveJob();
      } else {
        stopPolling();
      }
    }, 2000);
  }

  function stopPolling() {
    if (jobPollingInterval) {
      clearInterval(jobPollingInterval);
      jobPollingInterval = null;
    }
  }

  $effect(() => {
    fetchHealth();
    fetchUser();
    return () => {
      stopPolling();
    };
  });
</script>

<div class="space-y-8 max-w-5xl mx-auto pb-16">
  <!-- Welcome -->
  <section>
    <h1 class="text-3xl font-bold tracking-tight mb-2">BASI — AI Novel Translation</h1>
    <p class="text-gray-600 dark:text-gray-400">
      Balanced Automatic Story Intelligence. Fast literary novel translation with RAM-only BYOK keys, segment context preservation, and real-time state machine pipeline.
    </p>
  </section>

  <!-- Authentication Section -->
  <section class="rounded-xl border border-gray-200 dark:border-gray-800 bg-white dark:bg-gray-900 p-6 shadow-sm">
    <div class="flex items-center justify-between mb-4">
      <h2 class="text-xl font-semibold">User Authentication</h2>
      {#if user}
        <span class="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-300">
          Logged In
        </span>
      {:else}
        <span class="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium bg-gray-100 text-gray-800 dark:bg-gray-800 dark:text-gray-300">
          Not Authenticated
        </span>
      {/if}
    </div>

    {#if authLoading}
      <div class="text-sm text-gray-500">Checking authentication status…</div>
    {:else if user}
      <div class="space-y-4">
        <div class="grid grid-cols-1 sm:grid-cols-2 gap-4">
          <div class="p-3 bg-gray-50 dark:bg-gray-800 rounded-lg">
            <span class="text-xs text-gray-500 uppercase tracking-wide">Display Name</span>
            <div class="font-medium text-gray-900 dark:text-gray-100">{user.displayName}</div>
          </div>
          <div class="p-3 bg-gray-50 dark:bg-gray-800 rounded-lg">
            <span class="text-xs text-gray-500 uppercase tracking-wide">Email</span>
            <div class="font-medium text-gray-900 dark:text-gray-100">{user.email}</div>
          </div>
          <div class="p-3 bg-gray-50 dark:bg-gray-800 rounded-lg">
            <span class="text-xs text-gray-500 uppercase tracking-wide">User ID (GUID)</span>
            <div class="font-mono text-xs text-gray-700 dark:text-gray-300 truncate">{user.id}</div>
          </div>
          <div class="p-3 bg-gray-50 dark:bg-gray-800 rounded-lg">
            <span class="text-xs text-gray-500 uppercase tracking-wide">Account Created</span>
            <div class="text-sm text-gray-900 dark:text-gray-100">{new Date(user.createdAt).toLocaleDateString()}</div>
          </div>
        </div>

        <div class="flex justify-end">
          <button
            onclick={handleLogout}
            disabled={authLoading}
            class="px-4 py-2 bg-red-600 hover:bg-red-700 text-white text-sm font-medium rounded-lg transition-colors disabled:opacity-50"
          >
            Sign Out
          </button>
        </div>
      </div>
    {:else}
      <div class="space-y-4">
        <p class="text-sm text-gray-600 dark:text-gray-400">
          Please log in with your Google account to upload and translate novels securely.
        </p>
        <a
          href="/api/auth/login"
          class="inline-flex items-center gap-2 px-5 py-2.5 bg-blue-600 hover:bg-blue-700 text-white text-sm font-medium rounded-lg shadow-sm transition-colors"
        >
          <svg class="w-4 h-4" viewBox="0 0 24 24">
            <path fill="currentColor" d="M12.24 10.285V13.4h6.887C18.2 16.1 15.645 18 12.24 18c-3.315 0-6-2.685-6-6s2.685-6 6-6c1.47 0 2.815.535 3.86 1.415l2.36-2.36C16.89 3.555 14.71 2.5 12.24 2.5 6.995 2.5 2.75 6.745 2.75 12s4.245 9.5 9.49 9.5c5.485 0 9.11-3.86 9.11-9.27 0-.625-.065-1.225-.18-1.745H12.24z"/>
          </svg>
          Log in with Google
        </a>
      </div>
    {/if}

    {#if authError}
      <p class="mt-3 text-sm text-red-600 dark:text-red-400">{authError}</p>
    {/if}
  </section>

  {#if user}
    <!-- BYOK API Keys Section (ADR-004) -->
    <section class="rounded-xl border border-gray-200 dark:border-gray-800 bg-white dark:bg-gray-900 p-6 shadow-sm">
      <div class="flex items-center justify-between mb-3">
        <h2 class="text-xl font-semibold">BYOK API Keys (RAM-Only)</h2>
        <span class="text-xs bg-amber-100 text-amber-800 dark:bg-amber-900/60 dark:text-amber-300 px-2.5 py-1 rounded-full font-medium">
          Zero-Persistence Vault
        </span>
      </div>
      <p class="text-sm text-gray-600 dark:text-gray-400 mb-4">
        Per <strong>ADR-004</strong>, your API keys are retained exclusively in volatile server RAM and are never persisted to database or disk. Keys are lost when server restarts.
      </p>

      <!-- Active Key Status Badges -->
      <div class="grid grid-cols-1 sm:grid-cols-2 gap-4 mb-6">
        <div class="p-3.5 rounded-lg border border-gray-200 dark:border-gray-700 flex items-center justify-between">
          <div>
            <div class="font-medium text-sm">Google Gemini</div>
            <div class="text-xs text-gray-500">gemini-2.5-flash</div>
          </div>
          <div class="flex items-center gap-2">
            {#if apiKeyStatus['gemini']}
              <span class="px-2 py-0.5 rounded text-xs font-medium bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-300">
                Active in RAM
              </span>
              <button
                onclick={() => handleDeleteApiKey('gemini')}
                disabled={keyLoading}
                class="text-xs text-red-600 hover:text-red-800 underline"
              >
                Clear
              </button>
            {:else}
              <span class="px-2 py-0.5 rounded text-xs font-medium bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-400">
                Not Set
              </span>
            {/if}
          </div>
        </div>

        <div class="p-3.5 rounded-lg border border-gray-200 dark:border-gray-700 flex items-center justify-between">
          <div>
            <div class="font-medium text-sm">OpenAI</div>
            <div class="text-xs text-gray-500">gpt-4o-mini</div>
          </div>
          <div class="flex items-center gap-2">
            {#if apiKeyStatus['openai']}
              <span class="px-2 py-0.5 rounded text-xs font-medium bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-300">
                Active in RAM
              </span>
              <button
                onclick={() => handleDeleteApiKey('openai')}
                disabled={keyLoading}
                class="text-xs text-red-600 hover:text-red-800 underline"
              >
                Clear
              </button>
            {:else}
              <span class="px-2 py-0.5 rounded text-xs font-medium bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-400">
                Not Set
              </span>
            {/if}
          </div>
        </div>
      </div>

      <!-- Add Key Form -->
      <form onsubmit={handleSaveApiKey} class="space-y-4">
        <div class="grid grid-cols-1 sm:grid-cols-3 gap-3">
          <div>
            <label for="keyProvider" class="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1">
              Provider
            </label>
            <select
              id="keyProvider"
              bind:value={selectedProvider}
              class="w-full px-3 py-2 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-sm focus:ring-2 focus:ring-blue-500 focus:outline-none"
            >
              <option value="gemini">Google Gemini</option>
              <option value="openai">OpenAI</option>
            </select>
          </div>
          <div class="sm:col-span-2">
            <label for="apiKeyInput" class="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1">
              API Key
            </label>
            <input
              id="apiKeyInput"
              type="password"
              bind:value={inputApiKey}
              placeholder="Paste provider API key..."
              disabled={keyLoading}
              class="w-full px-3 py-2 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-sm focus:ring-2 focus:ring-blue-500 focus:outline-none font-mono"
            />
          </div>
        </div>

        {#if keyMessage}
          <div class="p-2.5 bg-green-50 dark:bg-green-950/30 border border-green-200 dark:border-green-900 rounded-lg text-xs text-green-700 dark:text-green-400">
            {keyMessage}
          </div>
        {/if}
        {#if keyError}
          <div class="p-2.5 bg-red-50 dark:bg-red-950/30 border border-red-200 dark:border-red-900 rounded-lg text-xs text-red-700 dark:text-red-400">
            {keyError}
          </div>
        {/if}

        <button
          type="submit"
          disabled={keyLoading || !inputApiKey.trim()}
          class="px-4 py-2 bg-blue-600 hover:bg-blue-700 text-white text-xs font-medium rounded-lg shadow-sm transition-colors disabled:opacity-50"
        >
          {keyLoading ? 'Saving...' : 'Set API Key in RAM'}
        </button>
      </form>
    </section>

    <!-- TXT Novel Upload Section -->
    <section class="rounded-xl border border-gray-200 dark:border-gray-800 bg-white dark:bg-gray-900 p-6 shadow-sm">
      <h2 class="text-xl font-semibold mb-2">Upload Novel (TXT)</h2>
      <p class="text-sm text-gray-600 dark:text-gray-400 mb-6">
        Select a raw plain text file (.txt, up to 10 MB). BASI detects encoding (UTF-8, Shift-JIS, GB18030, EUC-KR) and segments text into stable ordered units.
      </p>

      <form onsubmit={handleUpload} class="space-y-5">
        <div>
          <label for="novelFile" class="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
            TXT File (Max 10 MB)
          </label>
          <input
            id="novelFile"
            type="file"
            accept=".txt"
            onchange={handleFileChange}
            disabled={uploadLoading}
            class="block w-full text-sm text-gray-500 file:mr-4 file:py-2 file:px-4 file:rounded-lg file:border-0 file:text-sm file:font-semibold file:bg-blue-50 file:text-blue-700 hover:file:bg-blue-100 dark:file:bg-gray-800 dark:file:text-blue-400 cursor-pointer"
          />
        </div>

        <div class="grid grid-cols-1 sm:grid-cols-2 gap-4">
          <div>
            <label for="novelTitle" class="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
              Novel Title
            </label>
            <input
              id="novelTitle"
              type="text"
              bind:value={novelTitle}
              placeholder="e.g. Overlord Vol. 1"
              maxlength="512"
              disabled={uploadLoading}
              class="w-full px-3 py-2 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-gray-900 dark:text-gray-100 text-sm focus:ring-2 focus:ring-blue-500 focus:outline-none"
            />
          </div>

          <div>
            <label for="sourceLang" class="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
              Source Language
            </label>
            <select
              id="sourceLang"
              bind:value={sourceLanguage}
              disabled={uploadLoading}
              class="w-full px-3 py-2 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-gray-900 dark:text-gray-100 text-sm focus:ring-2 focus:ring-blue-500 focus:outline-none"
            >
              <option value="ja">Japanese (ja)</option>
              <option value="zh-CN">Simplified Chinese (zh-CN)</option>
              <option value="zh-TW">Traditional Chinese (zh-TW)</option>
              <option value="ko">Korean (ko)</option>
              <option value="en">English (en)</option>
            </select>
          </div>
        </div>

        {#if uploadError}
          <div class="p-3 bg-red-50 dark:bg-red-950/30 border border-red-200 dark:border-red-900 rounded-lg text-sm text-red-700 dark:text-red-400">
            {uploadError}
          </div>
        {/if}

        <button
          type="submit"
          disabled={uploadLoading || !selectedFile}
          class="px-5 py-2.5 bg-blue-600 hover:bg-blue-700 text-white text-sm font-medium rounded-lg shadow-sm transition-colors disabled:opacity-50 flex items-center gap-2"
        >
          {#if uploadLoading}
            <div class="w-4 h-4 border-2 border-white border-t-transparent rounded-full animate-spin"></div>
            <span>Parsing & Uploading…</span>
          {:else}
            <span>Upload & Segment Novel</span>
          {/if}
        </button>
      </form>

      {#if uploadSuccess}
        <div class="mt-6 p-4 bg-green-50 dark:bg-green-950/30 border border-green-200 dark:border-green-900 rounded-xl space-y-2">
          <div class="flex items-center gap-2 text-green-800 dark:text-green-300 font-semibold">
            <svg class="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M5 13l4 4L19 7"/>
            </svg>
            Novel Successfully Uploaded & Segmented!
          </div>
          <div class="grid grid-cols-2 sm:grid-cols-4 gap-3 text-sm pt-2">
            <div>
              <span class="text-xs text-gray-500 uppercase">Novel ID</span>
              <div class="font-mono text-xs text-gray-800 dark:text-gray-200 truncate">{uploadSuccess.novelId}</div>
            </div>
            <div>
              <span class="text-xs text-gray-500 uppercase">Title</span>
              <div class="font-medium text-gray-800 dark:text-gray-200">{uploadSuccess.title}</div>
            </div>
            <div>
              <span class="text-xs text-gray-500 uppercase">Language</span>
              <div class="font-medium text-gray-800 dark:text-gray-200">{uploadSuccess.sourceLanguage}</div>
            </div>
            <div>
              <span class="text-xs text-gray-500 uppercase">Segments Created</span>
              <div class="font-bold text-blue-600 dark:text-blue-400">{uploadSuccess.segmentCount}</div>
            </div>
          </div>
        </div>
      {/if}
    </section>

    <!-- Start Translation Section -->
    <section class="rounded-xl border border-gray-200 dark:border-gray-800 bg-white dark:bg-gray-900 p-6 shadow-sm">
      <h2 class="text-xl font-semibold mb-2">Start AI Translation</h2>
      <p class="text-sm text-gray-600 dark:text-gray-400 mb-6">
        Submit novel segments to the background translation worker with continuous context preservation.
      </p>

      {#if novels.length === 0}
        <p class="text-sm text-gray-500 italic">No uploaded novels found. Upload a novel above to begin translating.</p>
      {:else}
        <form onsubmit={handleStartTranslation} class="space-y-4">
          <div class="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <div>
              <label for="selectNovel" class="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
                Select Novel
              </label>
              <select
                id="selectNovel"
                bind:value={selectedNovelId}
                class="w-full px-3 py-2 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-sm focus:ring-2 focus:ring-blue-500 focus:outline-none"
              >
                {#each novels as nov}
                  <option value={nov.id}>
                    {nov.title} ({nov.sourceLanguage.toUpperCase()} - {nov.segmentCount} segments)
                  </option>
                {/each}
              </select>
            </div>

            <div>
              <label for="targetLang" class="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
                Target Language
              </label>
              <select
                id="targetLang"
                bind:value={targetLanguage}
                class="w-full px-3 py-2 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-sm focus:ring-2 focus:ring-blue-500 focus:outline-none"
              >
                <option value="vi">Vietnamese (vi)</option>
                <option value="en">English (en)</option>
                <option value="ja">Japanese (ja)</option>
                <option value="zh-CN">Chinese (zh-CN)</option>
                <option value="fr">French (fr)</option>
              </select>
            </div>
          </div>

          <div class="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <div>
              <label for="aiProvider" class="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
                AI Provider
              </label>
              <select
                id="aiProvider"
                value={translationProvider}
                onchange={(e) => onProviderChange((e.target as HTMLSelectElement).value as any)}
                class="w-full px-3 py-2 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-sm focus:ring-2 focus:ring-blue-500 focus:outline-none"
              >
                <option value="mock">Mock AI Provider (Testing / Instant)</option>
                <option value="gemini">Google Gemini (BYOK)</option>
                <option value="openai">OpenAI (BYOK)</option>
              </select>
            </div>

            <div>
              <label for="aiModel" class="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
                Model Name
              </label>
              <input
                id="aiModel"
                type="text"
                bind:value={translationModel}
                class="w-full px-3 py-2 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-sm focus:ring-2 focus:ring-blue-500 focus:outline-none font-mono"
              />
            </div>
          </div>

          {#if translationError}
            <div class="p-3 bg-red-50 dark:bg-red-950/30 border border-red-200 dark:border-red-900 rounded-lg text-sm text-red-700 dark:text-red-400">
              {translationError}
            </div>
          {/if}

          <button
            type="submit"
            disabled={translationLoading}
            class="px-5 py-2.5 bg-blue-600 hover:bg-blue-700 text-white text-sm font-medium rounded-lg shadow-sm transition-colors disabled:opacity-50 flex items-center gap-2"
          >
            {#if translationLoading}
              <div class="w-4 h-4 border-2 border-white border-t-transparent rounded-full animate-spin"></div>
              <span>Queuing Job...</span>
            {:else}
              <span>Queue Translation Job</span>
            {/if}
          </button>
        </form>
      {/if}
    </section>

    <!-- Active Translation Job Status & Bilingual Reader -->
    {#if activeJobDetail}
      <section class="rounded-xl border border-gray-200 dark:border-gray-800 bg-white dark:bg-gray-900 p-6 shadow-sm space-y-6">
        <div class="flex flex-wrap items-center justify-between gap-4 border-b border-gray-100 dark:border-gray-800 pb-4">
          <div>
            <div class="flex items-center gap-3">
              <h2 class="text-xl font-semibold">Translation Job</h2>
              <!-- State Machine Status Badge -->
              {#if activeJobDetail.status === 'Queued'}
                <span class="px-2.5 py-0.5 rounded-full text-xs font-medium bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-300 animate-pulse">
                  Queued
                </span>
              {:else if activeJobDetail.status === 'Processing'}
                <span class="px-2.5 py-0.5 rounded-full text-xs font-medium bg-indigo-100 text-indigo-800 dark:bg-indigo-900 dark:text-indigo-300 flex items-center gap-1.5">
                  <div class="w-2 h-2 rounded-full bg-indigo-600 animate-ping"></div>
                  Processing
                </span>
              {:else if activeJobDetail.status === 'WaitingForApiKey'}
                <span class="px-2.5 py-0.5 rounded-full text-xs font-medium bg-amber-100 text-amber-800 dark:bg-amber-900 dark:text-amber-300">
                  Waiting For API Key
                </span>
              {:else if activeJobDetail.status === 'Completed'}
                <span class="px-2.5 py-0.5 rounded-full text-xs font-medium bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-300">
                  Completed
                </span>
              {:else if activeJobDetail.status === 'Cancelled'}
                <span class="px-2.5 py-0.5 rounded-full text-xs font-medium bg-gray-100 text-gray-800 dark:bg-gray-800 dark:text-gray-300">
                  Cancelled
                </span>
              {:else if activeJobDetail.status === 'Failed'}
                <span class="px-2.5 py-0.5 rounded-full text-xs font-medium bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-300">
                  Failed
                </span>
              {/if}
            </div>
            <div class="text-xs text-gray-500 font-mono mt-1">ID: {activeJobDetail.id}</div>
          </div>

          <div class="flex items-center gap-2">
            <button
              onclick={refreshActiveJob}
              class="px-3 py-1.5 text-xs font-medium rounded-lg border border-gray-300 dark:border-gray-700 hover:bg-gray-50 dark:hover:bg-gray-800 transition-colors"
            >
              Refresh
            </button>
            {#if activeJobDetail.status === 'Queued' || activeJobDetail.status === 'Processing' || activeJobDetail.status === 'WaitingForApiKey'}
              <button
                onclick={() => handleCancelJob(activeJobDetail!.id)}
                class="px-3 py-1.5 text-xs font-medium rounded-lg bg-red-50 text-red-600 hover:bg-red-100 dark:bg-red-950/40 dark:text-red-400 transition-colors"
              >
                Cancel Job
              </button>
            {/if}
          </div>
        </div>

        {#if activeJobDetail.status === 'WaitingForApiKey'}
          <div class="p-4 bg-amber-50 dark:bg-amber-950/30 border border-amber-200 dark:border-amber-900 rounded-xl text-amber-800 dark:text-amber-300 text-sm">
            <div class="font-semibold mb-1">State Machine: Waiting for API Key in RAM</div>
            <p>
              This job is paused because no API key was found in RAM for provider <strong>{activeJobDetail.provider.toUpperCase()}</strong>.
              Add your key in the BYOK section above to automatically resume translation.
            </p>
          </div>
        {/if}

        {#if activeJobDetail.errorMessage}
          <div class="p-3 bg-red-50 dark:bg-red-950/30 border border-red-200 dark:border-red-900 rounded-lg text-sm text-red-700 dark:text-red-400">
            {activeJobDetail.errorMessage}
          </div>
        {/if}

        <!-- Progress Bar -->
        <div>
          <div class="flex justify-between text-xs font-medium text-gray-700 dark:text-gray-300 mb-1.5">
            <span>Progress: {activeJobDetail.completedSegments} of {activeJobDetail.totalSegments} segments completed</span>
            <span>{Math.round((activeJobDetail.completedSegments / Math.max(1, activeJobDetail.totalSegments)) * 100)}%</span>
          </div>
          <div class="w-full h-2.5 bg-gray-200 dark:bg-gray-700 rounded-full overflow-hidden">
            <div
              class="h-full bg-blue-600 transition-all duration-300 ease-out"
              style="width: {(activeJobDetail.completedSegments / Math.max(1, activeJobDetail.totalSegments)) * 100}%"
            ></div>
          </div>
          {#if activeJobDetail.totalTokens !== undefined && activeJobDetail.totalTokens > 0}
            <div class="mt-1.5 text-xs text-gray-500 dark:text-gray-400 flex gap-4">
              <span>Token Usage: <strong>{activeJobDetail.totalTokens.toLocaleString()}</strong> tokens</span>
              <span>(Prompt: {activeJobDetail.promptTokens?.toLocaleString() ?? 0}, Completion: {activeJobDetail.completionTokens?.toLocaleString() ?? 0})</span>
            </div>
          {/if}
        </div>

        <!-- Bilingual Reader Grid -->
        <div class="space-y-3">
          <h3 class="text-lg font-semibold tracking-tight">Bilingual Reader View</h3>
          <div class="divide-y divide-gray-100 dark:divide-gray-800 max-h-[500px] overflow-y-auto rounded-lg border border-gray-200 dark:border-gray-700">
            {#each activeJobDetail.segments as seg}
              <div class="p-4 grid grid-cols-1 md:grid-cols-2 gap-4 text-sm hover:bg-gray-50/50 dark:hover:bg-gray-800/30">
                <!-- Source Column -->
                <div class="space-y-1">
                  <div class="flex items-center gap-2">
                    <span class="text-xs font-mono font-medium text-gray-400">#{seg.order}</span>
                    <span class="text-xs uppercase text-gray-500 font-semibold">Source</span>
                  </div>
                  <p class="text-gray-800 dark:text-gray-200 whitespace-pre-wrap leading-relaxed">{seg.sourceText}</p>
                </div>

                <!-- Translated Column -->
                <div class="space-y-1 md:border-l md:border-gray-100 dark:md:border-gray-800 md:pl-4">
                  <div class="flex items-center justify-between">
                    <span class="text-xs uppercase text-blue-600 dark:text-blue-400 font-semibold">
                      Translated ({activeJobDetail.targetLanguage})
                    </span>
                    {#if seg.status === 'Completed'}
                      <span class="text-[10px] bg-green-100 text-green-800 dark:bg-green-900/60 dark:text-green-300 px-1.5 py-0.5 rounded">
                        Done
                      </span>
                    {:else if seg.status === 'Translating'}
                      <span class="text-[10px] bg-indigo-100 text-indigo-800 dark:bg-indigo-900/60 dark:text-indigo-300 px-1.5 py-0.5 rounded animate-pulse">
                        Translating...
                      </span>
                    {:else if seg.status === 'Failed'}
                      <span class="text-[10px] bg-red-100 text-red-800 dark:bg-red-900/60 dark:text-red-300 px-1.5 py-0.5 rounded">
                        Error
                      </span>
                    {:else}
                      <span class="text-[10px] bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-400 px-1.5 py-0.5 rounded">
                        Pending
                      </span>
                    {/if}
                  </div>
                  {#if seg.translatedText}
                    <p class="text-gray-900 dark:text-gray-100 font-medium whitespace-pre-wrap leading-relaxed">{seg.translatedText}</p>
                  {:else if seg.status === 'Translating'}
                    <div class="text-xs text-indigo-500 italic py-2">Translating segment with AI...</div>
                  {:else if seg.status === 'Failed'}
                    <div class="text-xs text-red-500 italic py-2">{seg.errorMessage || 'Failed to translate'}</div>
                  {:else}
                    <div class="text-xs text-gray-400 italic py-2">Waiting in queue...</div>
                  {/if}
                </div>
              </div>
            {/each}
          </div>
        </div>
      </section>
    {/if}

    <!-- Translation Jobs History -->
    {#if jobs.length > 0}
      <section class="rounded-xl border border-gray-200 dark:border-gray-800 bg-white dark:bg-gray-900 p-6 shadow-sm">
        <h2 class="text-xl font-semibold mb-3">Translation Jobs History</h2>
        <div class="overflow-x-auto">
          <table class="w-full text-left text-sm">
            <thead class="bg-gray-50 dark:bg-gray-800 text-xs text-gray-500 uppercase">
              <tr>
                <th class="px-4 py-2">Job ID</th>
                <th class="px-4 py-2">Target</th>
                <th class="px-4 py-2">Provider</th>
                <th class="px-4 py-2">Status</th>
                <th class="px-4 py-2">Progress</th>
                <th class="px-4 py-2">Actions</th>
              </tr>
            </thead>
            <tbody class="divide-y divide-gray-100 dark:divide-gray-800">
              {#each jobs as j}
                <tr class="hover:bg-gray-50/50 dark:hover:bg-gray-800/50">
                  <td class="px-4 py-2.5 font-mono text-xs">{j.id.slice(0, 8)}…</td>
                  <td class="px-4 py-2.5 font-medium">{j.targetLanguage.toUpperCase()}</td>
                  <td class="px-4 py-2.5 text-xs">{j.provider} ({j.model})</td>
                  <td class="px-4 py-2.5">
                    <span class="px-2 py-0.5 rounded text-xs font-medium {j.status === 'Completed' ? 'bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-300' : j.status === 'Processing' ? 'bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-300' : j.status === 'WaitingForApiKey' ? 'bg-amber-100 text-amber-800 dark:bg-amber-900 dark:text-amber-300' : 'bg-gray-100 text-gray-800 dark:bg-gray-800 dark:text-gray-300'}">
                      {j.status}
                    </span>
                  </td>
                  <td class="px-4 py-2.5 text-xs">{j.completedSegments}/{j.totalSegments}</td>
                  <td class="px-4 py-2.5">
                    <button
                      onclick={() => selectJob(j.id)}
                      class="text-xs text-blue-600 hover:text-blue-800 dark:text-blue-400 font-medium"
                    >
                      View
                    </button>
                  </td>
                </tr>
              {/each}
            </tbody>
          </table>
        </div>
      </section>
    {/if}
  {/if}

  <!-- Backend Health Status Section -->
  <section class="rounded-xl border border-gray-200 dark:border-gray-800 bg-gray-50 dark:bg-gray-900/50 p-6">
    <div class="flex items-center justify-between mb-4">
      <h2 class="text-lg font-semibold">System Diagnostics</h2>
      <button
        onclick={fetchHealth}
        disabled={healthLoading}
        class="text-sm px-3 py-1 rounded-md border border-gray-300 dark:border-gray-700 hover:bg-gray-100 dark:hover:bg-gray-800 disabled:opacity-50 transition-colors"
      >
        {healthLoading ? 'Checking…' : 'Refresh'}
      </button>
    </div>

    {#if healthLoading}
      <div class="flex items-center gap-2 text-gray-500 text-sm">
        <div class="w-3 h-3 rounded-full bg-gray-300 dark:bg-gray-600 animate-pulse"></div>
        <span>Checking backend status…</span>
      </div>
    {:else if healthError}
      <div class="space-y-3">
        <div class="flex items-center gap-2 text-red-600 dark:text-red-400">
          <div class="w-3 h-3 rounded-full bg-red-500"></div>
          <span class="font-medium">Unreachable</span>
        </div>
        <p class="text-sm text-red-600 dark:text-red-400 bg-red-50 dark:bg-red-950/30 rounded-lg px-3 py-2">
          {healthError}
        </p>
      </div>
    {:else if health}
      <div class="space-y-3">
        <div class="flex items-center gap-2">
          <div class="w-3 h-3 rounded-full {isHealthy ? 'bg-green-500' : 'bg-red-500'}"></div>
          <span class="font-medium {isHealthy ? 'text-green-700 dark:text-green-400' : 'text-red-700 dark:text-red-400'}">
            Backend API: {isHealthy ? 'Healthy' : 'Unhealthy'}
          </span>
        </div>

        <div class="grid grid-cols-1 sm:grid-cols-3 gap-4 mt-4">
          <div class="rounded-lg bg-white dark:bg-gray-800 border border-gray-200 dark:border-gray-700 px-4 py-3">
            <div class="text-xs text-gray-500 dark:text-gray-400 uppercase tracking-wide mb-1">API Version</div>
            <div class="font-mono text-sm">{health.version}</div>
          </div>
          <div class="rounded-lg bg-white dark:bg-gray-800 border border-gray-200 dark:border-gray-700 px-4 py-3">
            <div class="text-xs text-gray-500 dark:text-gray-400 uppercase tracking-wide mb-1">PostgreSQL Database</div>
            <div class="flex items-center gap-1.5">
              <div class="w-2 h-2 rounded-full {dbConnected ? 'bg-green-500' : 'bg-red-500'}"></div>
              <span class="text-sm capitalize">{health.database}</span>
            </div>
          </div>
          <div class="rounded-lg bg-white dark:bg-gray-800 border border-gray-200 dark:border-gray-700 px-4 py-3">
            <div class="text-xs text-gray-500 dark:text-gray-400 uppercase tracking-wide mb-1">Server Time</div>
            <div class="font-mono text-sm">{new Date(health.timestamp).toLocaleTimeString()}</div>
          </div>
        </div>
      </div>
    {/if}
  </section>
</div>
