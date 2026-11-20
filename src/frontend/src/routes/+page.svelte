<script lang="ts">
  import {
    checkHealth,
    getCurrentUser,
    logout,
    uploadNovel,
    type HealthResponse,
    type UserResponse,
    type UploadResponse
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
    } catch (e) {
      authError = e instanceof Error ? e.message : 'Failed to fetch user session';
      user = null;
    } finally {
      authLoading = false;
    }
  }

  async function handleLogout() {
    authLoading = true;
    authError = null;
    try {
      await logout();
      user = null;
      uploadSuccess = null;
    } catch (e) {
      authError = e instanceof Error ? e.message : 'Failed to logout';
    } finally {
      authLoading = false;
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
        // Auto-fill title from filename without extension
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
      // Reset form
      selectedFile = null;
      novelTitle = '';
    } catch (e) {
      uploadError = e instanceof Error ? e.message : 'Upload failed.';
    } finally {
      uploadLoading = false;
    }
  }

  $effect(() => {
    fetchHealth();
    fetchUser();
  });
</script>

<div class="space-y-8 max-w-4xl mx-auto">
  <!-- Welcome -->
  <section>
    <h1 class="text-3xl font-bold tracking-tight mb-2">BASI — AI Novel Translation</h1>
    <p class="text-gray-600 dark:text-gray-400">
      Balanced Automatic Story Intelligence. Upload raw novel text files, parse into segments with stable IDs, and prepare for AI translation.
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
          Please log in with your Google account to upload and manage your novels securely.
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

  <!-- TXT Novel Upload Section (Only visible when logged in) -->
  {#if user}
    <section class="rounded-xl border border-gray-200 dark:border-gray-800 bg-white dark:bg-gray-900 p-6 shadow-sm">
      <h2 class="text-xl font-semibold mb-2">Upload Novel (TXT)</h2>
      <p class="text-sm text-gray-600 dark:text-gray-400 mb-6">
        Select a raw plain text file (.txt, up to 10 MB). BASI will detect encoding (UTF-8, Shift-JIS, GB18030, EUC-KR) and segment the text into stable ordered units.
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
