<script lang="ts">
  import '../app.css';

  let { children } = $props();
</script>

<div class="min-h-screen flex flex-col bg-white text-gray-900 dark:bg-gray-950 dark:text-gray-100">
  <!-- Header -->
  <header class="border-b border-gray-200 dark:border-gray-800 bg-white dark:bg-gray-900">
    <nav class="max-w-5xl mx-auto px-4 py-3 flex items-center justify-between">
      <a href="/" class="flex items-center gap-2 text-lg font-bold tracking-tight">
        <span class="inline-flex items-center justify-center w-8 h-8 rounded-lg bg-indigo-600 text-white text-sm font-bold">B</span>
        <span>BASI</span>
      </a>
      <span class="text-xs text-gray-400 dark:text-gray-500">AI Translation Tool</span>
    </nav>
  </header>

  <!-- Main content -->
  <main class="flex-1 max-w-5xl mx-auto w-full px-4 py-8">
    {@render children()}
  </main>

  <!-- Footer -->
  <footer class="border-t border-gray-200 dark:border-gray-800 bg-white dark:bg-gray-900">
    <div class="max-w-5xl mx-auto px-4 py-3 text-center text-xs text-gray-400 dark:text-gray-500">
      BASI v0.1.0 &mdash; AI-Powered Novel Translation Tool
    </div>
  </footer>
</div>
