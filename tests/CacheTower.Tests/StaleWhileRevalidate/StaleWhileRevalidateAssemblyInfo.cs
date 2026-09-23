using Microsoft.VisualStudio.TestTools.UnitTesting;

//The stale-while-revalidate contract tests share the production clock seam (DateTimeProvider) via a fake clock,
//so the whole test assembly must not run test classes in parallel.
[assembly: DoNotParallelize]
