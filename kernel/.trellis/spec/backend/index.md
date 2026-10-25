# Backend Development Guidelines

> Best practices for backend development in this project.

---

## Overview

This directory contains guidelines for backend development. Fill in each file with your project's specific conventions.

---

## Guidelines Index

| Guide | Description | Status |
|-------|-------------|--------|
| [WFP 架构与决策](./wfp-architecture.md) | 已拍板的决策、组件划分、跨语言契约、未决清单 | 生效 |
| [WFP connected UDP record 泄漏](./wfp-connected-udp-record-refcount-leak.md) | 26100 实测、反汇编、影响与规避 | 生效 |
| [Forwarded 规则 blob](./forwarded-rule-blob.md) | Forwarded 规则层的跨语言 ABI、匹配语义与编译期校验 | 生效 |
| [Host 规则 blob（v2）](./host-rule-blob.md) | Host 规则层的跨语言 ABI、进程选择器语义、校验清单、block 边界与预算 | 生效 |
| [C++ 编码规范](./cpp-conventions.md) | 两套方言、命名、有效行上限、内核方言限制 | 生效 |
| [C# 编码规范](./csharp-conventions.md) | 工具链（net10.0）、.NET 命名、零依赖测试、ABI 布局断言 | 生效 |
| [Directory Structure](./directory-structure.md) | Module organization and file layout | To fill |
| [Database Guidelines](./database-guidelines.md) | 本仓库不使用数据库 | 不适用 |
| [Error Handling](./error-handling.md) | Error types, handling strategies | To fill |
| [Quality Guidelines](./quality-guidelines.md) | 质量标准的执行落点与门禁入口（指针，指向 cpp-conventions.md） | 生效 |
| [Logging Guidelines](./logging-guidelines.md) | Structured logging, log levels | To fill |

---

## How to Fill These Guidelines

For each guideline file:

1. Document your project's **actual conventions** (not ideals)
2. Include **code examples** from your codebase
3. List **forbidden patterns** and why
4. Add **common mistakes** your team has made

The goal is to help AI assistants and new team members understand how YOUR project works.

---

**Language**: 本项目文档以中文书写（2026-10-20 用户裁定）。
