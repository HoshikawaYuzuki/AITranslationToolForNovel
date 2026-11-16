<script lang="ts">
  import { checkHealth, type HealthResponse } from '$lib/api/client';

  let health = $state<HealthResponse | null>(null);
  let error = $state<string | null>(null);
  let loading = $state(true);

  let isHealthy = $derived(health?.status === 'healthy');
  let dbConnected = $derived(health?.database === 'connected');

  async function fetchHealth() {
    loading = true;
    error = null;
    try {
      health = await checkHealth();
    } catch (e) {
      error = e instanceof Error ? e.message : 'Failed to connect to backend';
      health = null;
    } finally {
      loading = false;
    }
  }

  $effect(() => {
    fetchHealth();
  });
</script>

<div class="space-y-8">
  <!-- Welcome -->
  <section>
    <h1 class="text-3xl font-bold tracking-tight mb-2">Welcome to BASI</h1>
    <p class="text-gray-600 dark:text-gray-400 max-w-2xl">
      BASI is an AI-powered translation tool designed for novels. It leverages large language models
      to produce context-aware, high-quality translations while preserving narrative style, character
      voice, and story continuity.
    </p>
  </section>

  <!-- Health Status -->
  <section class="rounded-xl border border-gray-200 dark:border-gray-800 bg-gray-50 dark:bg-gray-900 p-6">
    <div class="flex items-center justify-between mb-4">
      <h2 class="text-lg font-semibold">Backend Status</h2>
      <button
        onclick={fetchHealth}
        disabled={loading}
        class="text-sm px-3 py-1 rounded-md border border-gray-300 dark:border-gray-700 hover:bg-gray-100 dark:hover:bg-gray-800 disabled:opacity-50 transition-colors"
      >
        {loading ? 'Checking…' : 'Refresh'}
      </button>
    </div>

    {#if loading}
      <div class="flex items-center gap-2 text-gray-500">
        <div class="w-3 h-3 rounded-full bg-gray-300 dark:bg-gray-600 animate-pulse"></div>
        <span>Connecting to backend…</span>
      </div>
    {:else if error}
      <div class="space-y-3">
        <div class="flex items-center gap-2 text-red-600 dark:text-red-400">
          <div class="w-3 h-3 rounded-full bg-red-500"></div>
          <span class="font-medium">Unhealthy</span>
        </div>
        <p class="text-sm text-red-600 dark:text-red-400 bg-red-50 dark:bg-red-950/30 rounded-lg px-3 py-2">
          {error}
        </p>
      </div>
    {:else if health}
      <div class="space-y-3">
        <!-- Overall status -->
        <div class="flex items-center gap-2">
          <div class="w-3 h-3 rounded-full {isHealthy ? 'bg-green-500' : 'bg-red-500'}"></div>
          <span class="font-medium {isHealthy ? 'text-green-700 dark:text-green-400' : 'text-red-700 dark:text-red-400'}">
            {isHealthy ? 'Healthy' : 'Unhealthy'}
          </span>
        </div>

        <!-- Details grid -->
        <div class="grid grid-cols-1 sm:grid-cols-3 gap-4 mt-4">
          <div class="rounded-lg bg-white dark:bg-gray-800 border border-gray-200 dark:border-gray-700 px-4 py-3">
            <div class="text-xs text-gray-500 dark:text-gray-400 uppercase tracking-wide mb-1">Version</div>
            <div class="font-mono text-sm">{health.version}</div>
          </div>
          <div class="rounded-lg bg-white dark:bg-gray-800 border border-gray-200 dark:border-gray-700 px-4 py-3">
            <div class="text-xs text-gray-500 dark:text-gray-400 uppercase tracking-wide mb-1">Database</div>
            <div class="flex items-center gap-1.5">
              <div class="w-2 h-2 rounded-full {dbConnected ? 'bg-green-500' : 'bg-red-500'}"></div>
              <span class="text-sm capitalize">{health.database}</span>
            </div>
          </div>
          <div class="rounded-lg bg-white dark:bg-gray-800 border border-gray-200 dark:border-gray-700 px-4 py-3">
            <div class="text-xs text-gray-500 dark:text-gray-400 uppercase tracking-wide mb-1">Timestamp</div>
            <div class="font-mono text-sm">{new Date(health.timestamp).toLocaleTimeString()}</div>
          </div>
        </div>
      </div>
    {/if}
  </section>
</div>
