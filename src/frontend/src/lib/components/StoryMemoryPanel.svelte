<script lang="ts">
  import { onMount } from 'svelte';
  import {
    getStoryMemory,
    createStoryEntity,
    updateStoryEntity,
    deleteStoryEntity,
    extractStoryMemory,
    getJobConsistency,
    type StoryEntityCategory,
    type StoryEntityItem,
    type ExtractStoryMemoryResponse,
    type StoryConsistencyReport
  } from '$lib/api/client';

  let {
    novelId,
    novelTitle,
    targetLanguage,
    activeJobId = null
  }: {
    novelId: string;
    novelTitle: string;
    targetLanguage: string;
    activeJobId?: string | null;
  } = $props();

  const CATEGORIES: StoryEntityCategory[] = [
    'Character',
    'Location',
    'Skill',
    'Item',
    'Organization',
    'Concept',
    'Honorific',
    'Other'
  ];

  // Glossary list & filter state
  let entries = $state<StoryEntityItem[]>([]);
  let loading = $state(false);
  let error = $state<string | null>(null);
  let searchQuery = $state('');
  let categoryFilter = $state<string>('');
  let latestLoadToken = 0;

  // Extraction state
  let extractProvider = $state<'mock' | 'gemini' | 'openai'>('mock');
  let extractModel = $state('mock-v1');
  let extractStartOrder = $state<string>('');
  let extractEndOrder = $state<string>('');
  let extractApiKey = $state('');
  let extracting = $state(false);
  let extractError = $state<string | null>(null);
  let extractSummary = $state<ExtractStoryMemoryResponse | null>(null);

  // Add manual entry form state
  let newSourceTerm = $state('');
  let newTranslatedTerm = $state('');
  let newCategory = $state<StoryEntityCategory>('Character');
  let newAliasesInput = $state('');
  let newDescription = $state('');
  let newIsLocked = $state(true);
  let creating = $state(false);
  let createError = $state<string | null>(null);

  // Inline edit state
  let editingId = $state<string | null>(null);
  let editSourceTerm = $state('');
  let editTranslatedTerm = $state('');
  let editCategory = $state<StoryEntityCategory>('Character');
  let editAliasesInput = $state('');
  let editDescription = $state('');
  let editIsLocked = $state(true);
  let savingEdit = $state(false);
  let editError = $state<string | null>(null);

  // Consistency check state (scoped cleanly per activeJobId without $effect state resets)
  let consistencyResult = $state<{ jobId: string; report: StoryConsistencyReport } | null>(null);
  let consistencyErrorState = $state<{ jobId: string; message: string } | null>(null);
  let consistencyLoading = $state(false);
  let consistencyReport = $derived(
    consistencyResult && consistencyResult.jobId === activeJobId ? consistencyResult.report : null
  );
  let consistencyError = $derived(
    consistencyErrorState && consistencyErrorState.jobId === activeJobId ? consistencyErrorState.message : null
  );

  let filteredEntries = $derived.by(() => {
    const q = searchQuery.trim().toLowerCase();
    return entries.filter((item) => {
      if (categoryFilter && item.category !== categoryFilter) return false;
      if (!q) return true;
      return (
        item.sourceTerm.toLowerCase().includes(q) ||
        item.translatedTerm.toLowerCase().includes(q) ||
        (item.description?.toLowerCase().includes(q) ?? false) ||
        item.aliases.some((a) => a.toLowerCase().includes(q))
      );
    });
  });

  function parseAliases(raw: string): string[] {
    return raw
      .split(',')
      .map((s) => s.trim())
      .filter((s) => s.length > 0);
  }

  function onExtractProviderChange(prov: 'mock' | 'gemini' | 'openai') {
    extractProvider = prov;
    if (prov === 'gemini') extractModel = 'gemini-2.5-flash';
    else if (prov === 'openai') extractModel = 'gpt-4o-mini';
    else extractModel = 'mock-v1';
  }

  async function loadGlossary() {
    if (!novelId || !targetLanguage) return;
    const token = ++latestLoadToken;
    loading = true;
    error = null;
    try {
      const data = await getStoryMemory(novelId, { targetLanguage });
      if (token === latestLoadToken) {
        entries = data;
      }
    } catch (e) {
      if (token === latestLoadToken) {
        error = e instanceof Error ? e.message : 'Failed to load story memory.';
      }
    } finally {
      if (token === latestLoadToken) {
        loading = false;
      }
    }
  }

  async function handleExtract(e: SubmitEvent) {
    e.preventDefault();
    if (!novelId || !targetLanguage) return;

    extracting = true;
    extractError = null;
    extractSummary = null;

    const startOrder = extractStartOrder.trim() === '' ? null : Number.parseInt(extractStartOrder, 10);
    const endOrder = extractEndOrder.trim() === '' ? null : Number.parseInt(extractEndOrder, 10);

    try {
      const result = await extractStoryMemory(novelId, {
        targetLanguage,
        provider: extractProvider,
        model: extractModel.trim() || undefined,
        startOrder: Number.isNaN(startOrder) ? null : startOrder,
        endOrder: Number.isNaN(endOrder) ? null : endOrder,
        apiKey: extractApiKey.trim() || undefined
      });
      extractApiKey = '';
      extractSummary = result;
      entries = result.glossary;
    } catch (err) {
      extractError = err instanceof Error ? err.message : 'Story memory extraction failed.';
    } finally {
      extracting = false;
    }
  }

  async function handleCreateEntry(e: SubmitEvent) {
    e.preventDefault();
    if (!newSourceTerm.trim() || !newTranslatedTerm.trim()) {
      createError = 'Both source term and canonical translation are required.';
      return;
    }
    creating = true;
    createError = null;
    try {
      const created = await createStoryEntity(novelId, {
        targetLanguage,
        sourceTerm: newSourceTerm.trim(),
        translatedTerm: newTranslatedTerm.trim(),
        category: newCategory,
        aliases: parseAliases(newAliasesInput),
        description: newDescription.trim() || null,
        isLocked: newIsLocked
      });
      entries = [...entries, created];
      newSourceTerm = '';
      newTranslatedTerm = '';
      newAliasesInput = '';
      newDescription = '';
      newIsLocked = true;
    } catch (err) {
      createError = err instanceof Error ? err.message : 'Failed to add glossary entry.';
    } finally {
      creating = false;
    }
  }

  function startEdit(item: StoryEntityItem) {
    editingId = item.id;
    editSourceTerm = item.sourceTerm;
    editTranslatedTerm = item.translatedTerm;
    editCategory = item.category;
    editAliasesInput = item.aliases.join(', ');
    editDescription = item.description ?? '';
    editIsLocked = item.isLocked;
    editError = null;
  }

  function cancelEdit() {
    editingId = null;
    editError = null;
  }

  async function saveEdit(item: StoryEntityItem) {
    if (!editSourceTerm.trim() || !editTranslatedTerm.trim()) {
      editError = 'Source term and canonical translation cannot be empty.';
      return;
    }
    savingEdit = true;
    editError = null;
    try {
      const updated = await updateStoryEntity(novelId, item.id, {
        targetLanguage: item.targetLanguage,
        sourceTerm: editSourceTerm.trim(),
        translatedTerm: editTranslatedTerm.trim(),
        category: editCategory,
        aliases: parseAliases(editAliasesInput),
        description: editDescription.trim() || null,
        isLocked: editIsLocked,
        firstSeenOrder: item.firstSeenOrder ?? null
      });
      entries = entries.map((x) => (x.id === item.id ? updated : x));
      editingId = null;
    } catch (err) {
      editError = err instanceof Error ? err.message : 'Failed to save changes.';
    } finally {
      savingEdit = false;
    }
  }

  async function toggleLock(item: StoryEntityItem) {
    error = null;
    try {
      const updated = await updateStoryEntity(novelId, item.id, {
        targetLanguage: item.targetLanguage,
        sourceTerm: item.sourceTerm,
        translatedTerm: item.translatedTerm,
        category: item.category,
        aliases: item.aliases,
        description: item.description ?? null,
        isLocked: !item.isLocked,
        firstSeenOrder: item.firstSeenOrder ?? null
      });
      entries = entries.map((x) => (x.id === item.id ? updated : x));
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to toggle lock.';
    }
  }

  async function handleDelete(item: StoryEntityItem) {
    error = null;
    try {
      await deleteStoryEntity(novelId, item.id);
      entries = entries.filter((x) => x.id !== item.id);
      if (editingId === item.id) editingId = null;
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to delete entry.';
    }
  }

  async function handleConsistencyCheck() {
    if (!activeJobId) return;
    const jobIdAtCall = activeJobId;
    consistencyLoading = true;
    consistencyErrorState = null;
    try {
      const report = await getJobConsistency(jobIdAtCall);
      consistencyResult = { jobId: jobIdAtCall, report };
    } catch (err) {
      consistencyErrorState = {
        jobId: jobIdAtCall,
        message: err instanceof Error ? err.message : 'Consistency check failed.'
      };
    } finally {
      consistencyLoading = false;
    }
  }

  onMount(() => {
    void loadGlossary();
  });
</script>

<section class="rounded-xl border border-gray-200 dark:border-gray-800 bg-white dark:bg-gray-900 p-6 shadow-sm space-y-6">
  <div class="flex flex-wrap items-center justify-between gap-3 border-b border-gray-100 dark:border-gray-800 pb-4">
    <div>
      <h2 class="text-xl font-semibold">Story Memory & Glossary (BASI-004)</h2>
      <p class="text-sm text-gray-600 dark:text-gray-400">
        Curate canonical names and terminology for <strong>{novelTitle}</strong> ({targetLanguage.toUpperCase()}). Matched glossary terms are automatically injected into translation prompts.
      </p>
    </div>
    <button
      type="button"
      onclick={loadGlossary}
      disabled={loading}
      class="px-3 py-1.5 text-xs font-medium rounded-lg border border-gray-300 dark:border-gray-700 hover:bg-gray-50 dark:hover:bg-gray-800 transition-colors disabled:opacity-50"
    >
      {loading ? 'Refreshing...' : `Refresh (${entries.length})`}
    </button>
  </div>

  <!-- AI / Mock Extraction Panel -->
  <form onsubmit={handleExtract} class="p-4 rounded-xl bg-gray-50 dark:bg-gray-800/50 border border-gray-200 dark:border-gray-700 space-y-3">
    <div class="flex flex-wrap items-center justify-between gap-2">
      <h3 class="text-sm font-semibold">Extract Entities from Novel Segments</h3>
      <span class="text-xs text-gray-500">Up to 20 segments / 12,000 chars per call; locked entries stay protected</span>
    </div>

    <div class="grid grid-cols-1 sm:grid-cols-4 gap-3">
      <div>
        <label for="extractProvider" class="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1">
          Provider
        </label>
        <select
          id="extractProvider"
          value={extractProvider}
          onchange={(e) => onExtractProviderChange((e.target as HTMLSelectElement).value as 'mock' | 'gemini' | 'openai')}
          class="w-full px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs focus:ring-2 focus:ring-blue-500 focus:outline-none"
        >
          <option value="mock">Mock Provider</option>
          <option value="gemini">Google Gemini (BYOK)</option>
          <option value="openai">OpenAI (BYOK)</option>
        </select>
      </div>

      <div>
        <label for="extractModel" class="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1">
          Model
        </label>
        <input
          id="extractModel"
          type="text"
          bind:value={extractModel}
          class="w-full px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs font-mono focus:ring-2 focus:ring-blue-500 focus:outline-none"
        />
      </div>

      <div>
        <label for="extractStartOrder" class="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1">
          Start Segment #
        </label>
        <input
          id="extractStartOrder"
          type="number"
          min="0"
          placeholder="Auto (0)"
          bind:value={extractStartOrder}
          class="w-full px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs focus:ring-2 focus:ring-blue-500 focus:outline-none"
        />
      </div>

      <div>
        <label for="extractEndOrder" class="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1">
          End Segment #
        </label>
        <input
          id="extractEndOrder"
          type="number"
          min="0"
          placeholder="Auto window"
          bind:value={extractEndOrder}
          class="w-full px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs focus:ring-2 focus:ring-blue-500 focus:outline-none"
        />
      </div>
    </div>

    {#if extractProvider !== 'mock'}
      <div>
        <label for="extractApiKey" class="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1">
          Ephemeral Request API Key <span class="text-gray-500 font-normal">(Optional — falls back to account key in RAM; never stored)</span>
        </label>
        <input
          id="extractApiKey"
          type="password"
          bind:value={extractApiKey}
          placeholder={`Optional ${extractProvider.toUpperCase()} key for this extraction call...`}
          class="w-full px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs font-mono focus:ring-2 focus:ring-blue-500 focus:outline-none"
        />
      </div>
    {/if}

    {#if extractError}
      <div class="p-2.5 bg-red-50 dark:bg-red-950/30 border border-red-200 dark:border-red-900 rounded-lg text-xs text-red-700 dark:text-red-400">
        {extractError}
      </div>
    {/if}

    {#if extractSummary}
      <div class="p-2.5 bg-green-50 dark:bg-green-950/30 border border-green-200 dark:border-green-900 rounded-lg text-xs text-green-800 dark:text-green-300">
        Extracted {extractSummary.extractedCount} candidates ({extractSummary.created} created, {extractSummary.updated} updated, {extractSummary.skippedLocked} locked skipped) · {extractSummary.totalTokens} tokens ({extractSummary.modelUsed}).
      </div>
    {/if}

    <button
      type="submit"
      disabled={extracting}
      class="px-4 py-2 bg-indigo-600 hover:bg-indigo-700 text-white text-xs font-medium rounded-lg shadow-sm transition-colors disabled:opacity-50"
    >
      {extracting ? 'Extracting Entities...' : 'Extract Characters & Terms'}
    </button>
  </form>

  <!-- Manual Add Entry Form -->
  <form onsubmit={handleCreateEntry} class="p-4 rounded-xl border border-gray-200 dark:border-gray-700 space-y-3">
    <h3 class="text-sm font-semibold">Add Canonical Glossary Entry</h3>

    <div class="grid grid-cols-1 sm:grid-cols-3 gap-3">
      <div>
        <label for="newSourceTerm" class="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1">
          Source Term
        </label>
        <input
          id="newSourceTerm"
          type="text"
          bind:value={newSourceTerm}
          placeholder="e.g. アインズ"
          maxlength="256"
          class="w-full px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs focus:ring-2 focus:ring-blue-500 focus:outline-none"
        />
      </div>

      <div>
        <label for="newTranslatedTerm" class="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1">
          Canonical Translation ({targetLanguage})
        </label>
        <input
          id="newTranslatedTerm"
          type="text"
          bind:value={newTranslatedTerm}
          placeholder="e.g. Ainz"
          maxlength="256"
          class="w-full px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs focus:ring-2 focus:ring-blue-500 focus:outline-none"
        />
      </div>

      <div>
        <label for="newCategory" class="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1">
          Category
        </label>
        <select
          id="newCategory"
          bind:value={newCategory}
          class="w-full px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs focus:ring-2 focus:ring-blue-500 focus:outline-none"
        >
          {#each CATEGORIES as cat (cat)}
            <option value={cat}>{cat}</option>
          {/each}
        </select>
      </div>
    </div>

    <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
      <div>
        <label for="newAliases" class="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1">
          Aliases <span class="text-gray-500 font-normal">(comma-separated)</span>
        </label>
        <input
          id="newAliases"
          type="text"
          bind:value={newAliasesInput}
          placeholder="e.g. モモンガ, 鈴木悟"
          class="w-full px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs focus:ring-2 focus:ring-blue-500 focus:outline-none"
        />
      </div>

      <div>
        <label for="newDescription" class="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1">
          Context Note (Tone / Pronoun / Lore)
        </label>
        <input
          id="newDescription"
          type="text"
          bind:value={newDescription}
          placeholder="e.g. Protagonist, male, formal sovereign tone"
          maxlength="1000"
          class="w-full px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs focus:ring-2 focus:ring-blue-500 focus:outline-none"
        />
      </div>
    </div>

    <div class="flex flex-wrap items-center justify-between gap-3">
      <label class="inline-flex items-center gap-2 text-xs text-gray-700 dark:text-gray-300 cursor-pointer">
        <input type="checkbox" bind:checked={newIsLocked} class="rounded border-gray-300 text-blue-600" />
        Lock entry (prevent AI re-extraction from overwriting)
      </label>

      <button
        type="submit"
        disabled={creating}
        class="px-4 py-1.5 bg-blue-600 hover:bg-blue-700 text-white text-xs font-medium rounded-lg shadow-sm transition-colors disabled:opacity-50"
      >
        {creating ? 'Adding...' : 'Add Entry'}
      </button>
    </div>

    {#if createError}
      <div class="p-2 bg-red-50 dark:bg-red-950/30 border border-red-200 dark:border-red-900 rounded-lg text-xs text-red-700 dark:text-red-400">
        {createError}
      </div>
    {/if}
  </form>

  <!-- Filter & Glossary List -->
  <div class="space-y-3">
    <div class="flex flex-wrap items-center gap-3">
      <input
        type="search"
        bind:value={searchQuery}
        placeholder="Search source term, translation, aliases, notes..."
        class="flex-1 min-w-[200px] px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs focus:ring-2 focus:ring-blue-500 focus:outline-none"
      />
      <select
        bind:value={categoryFilter}
        class="px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs focus:ring-2 focus:ring-blue-500 focus:outline-none"
      >
        <option value="">All Categories</option>
        {#each CATEGORIES as cat (cat)}
          <option value={cat}>{cat}</option>
        {/each}
      </select>
    </div>

    {#if error}
      <div class="p-2.5 bg-red-50 dark:bg-red-950/30 border border-red-200 dark:border-red-900 rounded-lg text-xs text-red-700 dark:text-red-400">
        {error}
      </div>
    {/if}

    {#if filteredEntries.length === 0}
      <div class="p-6 text-center text-xs text-gray-500 border border-dashed border-gray-200 dark:border-gray-700 rounded-lg">
        {entries.length === 0
          ? 'No story memory entries for this novel & language yet. Extract terms above or add manually.'
          : 'No glossary entries matched your filter.'}
      </div>
    {:else}
      <div class="divide-y divide-gray-100 dark:divide-gray-800 rounded-lg border border-gray-200 dark:border-gray-700 max-h-[380px] overflow-y-auto">
        {#each filteredEntries as item (item.id)}
          <div class="p-3.5 text-xs space-y-2 hover:bg-gray-50/50 dark:hover:bg-gray-800/30">
            {#if editingId === item.id}
              <div class="space-y-2">
                <div class="grid grid-cols-1 sm:grid-cols-3 gap-2">
                  <input
                    type="text"
                    bind:value={editSourceTerm}
                    class="px-2.5 py-1 border border-gray-300 dark:border-gray-700 rounded bg-white dark:bg-gray-800 text-xs"
                  />
                  <input
                    type="text"
                    bind:value={editTranslatedTerm}
                    class="px-2.5 py-1 border border-gray-300 dark:border-gray-700 rounded bg-white dark:bg-gray-800 text-xs"
                  />
                  <select
                    bind:value={editCategory}
                    class="px-2.5 py-1 border border-gray-300 dark:border-gray-700 rounded bg-white dark:bg-gray-800 text-xs"
                  >
                    {#each CATEGORIES as cat (cat)}
                      <option value={cat}>{cat}</option>
                    {/each}
                  </select>
                </div>
                <div class="grid grid-cols-1 sm:grid-cols-2 gap-2">
                  <input
                    type="text"
                    bind:value={editAliasesInput}
                    placeholder="Aliases (comma-separated)"
                    class="px-2.5 py-1 border border-gray-300 dark:border-gray-700 rounded bg-white dark:bg-gray-800 text-xs"
                  />
                  <input
                    type="text"
                    bind:value={editDescription}
                    placeholder="Context note"
                    class="px-2.5 py-1 border border-gray-300 dark:border-gray-700 rounded bg-white dark:bg-gray-800 text-xs"
                  />
                </div>
                {#if editError}
                  <p class="text-red-600 dark:text-red-400">{editError}</p>
                {/if}
                <div class="flex items-center justify-between">
                  <label class="inline-flex items-center gap-1.5">
                    <input type="checkbox" bind:checked={editIsLocked} />
                    Locked
                  </label>
                  <div class="flex gap-2">
                    <button
                      type="button"
                      onclick={cancelEdit}
                      class="px-2.5 py-1 rounded border border-gray-300 dark:border-gray-700 hover:bg-gray-100 dark:hover:bg-gray-800"
                    >
                      Cancel
                    </button>
                    <button
                      type="button"
                      disabled={savingEdit}
                      onclick={() => saveEdit(item)}
                      class="px-2.5 py-1 rounded bg-blue-600 text-white hover:bg-blue-700 disabled:opacity-50"
                    >
                      {savingEdit ? 'Saving...' : 'Save'}
                    </button>
                  </div>
                </div>
              </div>
            {:else}
              <div class="flex flex-wrap items-start justify-between gap-2">
                <div class="space-y-1">
                  <div class="flex flex-wrap items-center gap-2">
                    <span class="font-semibold text-sm text-gray-900 dark:text-gray-100">{item.sourceTerm}</span>
                    <span class="text-gray-400">-&gt;</span>
                    <span class="font-semibold text-sm text-blue-700 dark:text-blue-400">{item.translatedTerm}</span>
                    <span class="px-1.5 py-0.5 rounded bg-gray-100 dark:bg-gray-800 text-gray-700 dark:text-gray-300 text-[11px]">
                      {item.category}
                    </span>
                    <span class="px-1.5 py-0.5 rounded text-[11px] {item.origin === 'Manual' ? 'bg-purple-100 text-purple-800 dark:bg-purple-900/50 dark:text-purple-300' : 'bg-cyan-100 text-cyan-800 dark:bg-cyan-900/50 dark:text-cyan-300'}">
                      {item.origin}
                    </span>
                    {#if item.isLocked}
                      <span class="px-1.5 py-0.5 rounded bg-amber-100 text-amber-800 dark:bg-amber-900/50 dark:text-amber-300 text-[11px]">
                        Locked
                      </span>
                    {/if}
                  </div>
                  {#if item.aliases.length > 0}
                    <div class="text-gray-500 dark:text-gray-400">
                      Aliases: {item.aliases.join(', ')}
                    </div>
                  {/if}
                  {#if item.description}
                    <div class="text-gray-600 dark:text-gray-300 italic">{item.description}</div>
                  {/if}
                </div>

                <div class="flex items-center gap-2">
                  <button
                    type="button"
                    onclick={() => toggleLock(item)}
                    class="text-amber-600 hover:text-amber-800 dark:text-amber-400 underline"
                  >
                    {item.isLocked ? 'Unlock' : 'Lock'}
                  </button>
                  <button
                    type="button"
                    onclick={() => startEdit(item)}
                    class="text-blue-600 hover:text-blue-800 dark:text-blue-400 underline"
                  >
                    Edit
                  </button>
                  <button
                    type="button"
                    onclick={() => handleDelete(item)}
                    class="text-red-600 hover:text-red-800 dark:text-red-400 underline"
                  >
                    Delete
                  </button>
                </div>
              </div>
            {/if}
          </div>
        {/each}
      </div>
    {/if}
  </div>

  <!-- Consistency Check Diagnostic -->
  {#if activeJobId}
    <div class="pt-4 border-t border-gray-100 dark:border-gray-800 space-y-3">
      <div class="flex flex-wrap items-center justify-between gap-2">
        <div>
          <h3 class="text-sm font-semibold">Entity Consistency Check</h3>
          <p class="text-xs text-gray-500">Compare active translation job segments against canonical glossary terms.</p>
        </div>
        <button
          type="button"
          onclick={handleConsistencyCheck}
          disabled={consistencyLoading}
          class="px-3 py-1.5 text-xs font-medium rounded-lg border border-blue-300 dark:border-blue-800 text-blue-700 dark:text-blue-300 hover:bg-blue-50 dark:hover:bg-blue-950/30 transition-colors disabled:opacity-50"
        >
          {consistencyLoading ? 'Checking...' : 'Check Consistency'}
        </button>
      </div>

      {#if consistencyError}
        <div class="p-2.5 bg-red-50 dark:bg-red-950/30 border border-red-200 dark:border-red-900 rounded-lg text-xs text-red-700 dark:text-red-400">
          {consistencyError}
        </div>
      {/if}

      {#if consistencyReport}
        {#if consistencyReport.issueCount === 0}
          <div class="p-3 bg-green-50 dark:bg-green-950/30 border border-green-200 dark:border-green-900 rounded-lg text-xs text-green-800 dark:text-green-300">
            Checked {consistencyReport.checkedSegments} completed segments against {consistencyReport.glossaryCount} glossary entries: 0 consistency issues found.
          </div>
        {:else}
          <div class="p-3 bg-amber-50 dark:bg-amber-950/30 border border-amber-200 dark:border-amber-900 rounded-lg text-xs text-amber-900 dark:text-amber-200 space-y-1.5">
            <div class="font-semibold">
              Found {consistencyReport.issueCount} potential term consistency mismatch(es) across {consistencyReport.checkedSegments} segments:
            </div>
            <ul class="list-disc pl-5 space-y-1 max-h-40 overflow-y-auto">
              {#each consistencyReport.issues as issue (`${issue.segmentOrder}-${issue.entityId}`)}
                <li>
                  Segment #{issue.segmentOrder}: matched <strong>{issue.matchedSourceTerms.join(', ')}</strong> [{issue.category}], expected canonical translation <strong>{issue.expectedTranslation}</strong>.
                </li>
              {/each}
            </ul>
          </div>
        {/if}
      {/if}
    </div>
  {/if}
</section>
