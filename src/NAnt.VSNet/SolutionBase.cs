// NAnt - A .NET build tool
// Copyright (C) 2001-2003 Gerry Shaw
//
// This program is free software; you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation; either version 2 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program; if not, write to the Free Software
// Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA
//
// Matthew Mastracci (matt@aclaro.com)

using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;

using NAnt.Core;
using NAnt.Core.Types;
using NAnt.Core.Util;

using NAnt.VSNet.Tasks;
using NAnt.VSNet.Types;

namespace NAnt.VSNet {
    public abstract class SolutionBase {
        #region Protected Instance Constructors

        protected SolutionBase(SolutionTask solutionTask, TempFileCollection tfc, GacCache gacCache, ReferencesResolver refResolver) : this(tfc, solutionTask) {
            if (solutionTask.SolutionFile != null) {
                _file = solutionTask.SolutionFile;
            } else {
                LoadProjectGuids(new ArrayList(solutionTask.Projects.FileNames), false);
                LoadProjectGuids(new ArrayList(solutionTask.ReferenceProjects.FileNames), true);
                LoadProjects(gacCache, refResolver);
                GetDependenciesFromProjects();
            }
        }

        #endregion Protected Instance Constructors

        #region Private Instance Constructors

        private SolutionBase(TempFileCollection tfc, SolutionTask solutionTask) {
            _htProjects = CollectionsUtil.CreateCaseInsensitiveHashtable();
            _htProjectDirectories = CollectionsUtil.CreateCaseInsensitiveHashtable();
            _htOutputFiles = CollectionsUtil.CreateCaseInsensitiveHashtable();
            _htProjectFiles = CollectionsUtil.CreateCaseInsensitiveHashtable();
            _htProjectDependencies = CollectionsUtil.CreateCaseInsensitiveHashtable();
            _htProjectBuildConfigurations = CollectionsUtil.CreateCaseInsensitiveHashtable();
            _htReferenceProjects = CollectionsUtil.CreateCaseInsensitiveHashtable();
            _tfc = tfc;
            _solutionTask = solutionTask;
            _outputDir = solutionTask.OutputDir;
            _webMaps = solutionTask.WebMaps;
            ProjectFactory.ClearCache();
        }

        #endregion Private Instance Constructors

        #region Public Instance Properties

        public FileInfo File {
            get { return _file; }
        }

        public TempFileCollection TemporaryFiles {
            get { return _tfc; }
        }

        #endregion Public Instance Properties

        #region Protected Instance Properties {

        protected WebMapCollection WebMaps {
            get { return _webMaps; }
        }

        protected Hashtable ProjectFiles {
            get { return _htProjectFiles; }
        }

        protected Hashtable ProjectBuildConfigurations {
            get { return _htProjectBuildConfigurations; }
        }

        #endregion Protected Instance Properties {

        #region Public Instance Methods

        public void RecursiveLoadTemplateProject(string fileName) {
            XmlDocument doc = ProjectFactory.LoadProjectXml(fileName);

            foreach (XmlNode node in doc.SelectNodes("//Reference")) {
                XmlNode projectGuidNode = node.SelectSingleNode("GUIDPROJECTID");
                XmlNode fileNode = node.SelectSingleNode("FILE");

                if (fileNode == null) {
                    Log(Level.Warning, "Reference with missing <FILE> node. Skipping.");
                    continue;
                }

                // check if we're dealing with project or assembly reference
                if (projectGuidNode != null) {
                    string subProjectFilename = node.SelectSingleNode("FILE").InnerText;
                    string fullPath;

                    // translate URLs to physical paths if using a webmap
                    string map = _webMaps.FindBestMatch(subProjectFilename);
                    if (map != null) {
                        Log(Level.Debug, "Found webmap match '{0}' for '{1}.", 
                            map, subProjectFilename);
                        subProjectFilename = map;
                    }

                    try {
                        Uri uri = new Uri(subProjectFilename);
                        if (uri.Scheme == Uri.UriSchemeFile) {
                            fullPath = Path.Combine(Path.GetDirectoryName(fileName), uri.LocalPath);
                        } else {
                            fullPath = subProjectFilename;

                            if (!_solutionTask.EnableWebDav) {
                                throw new BuildException(string.Format(CultureInfo.InvariantCulture,
                                    "Cannot build web project '{0}'.  Please use" 
                                    + " <webmap> to map the given URL to a project-relative" 
                                    + " path, or specify enablewebdav=\"true\" on the" 
                                    + " <solution> task element to use WebDAV.", fullPath));
                            }
                        }
                    } catch (UriFormatException) {
                        fullPath = Path.Combine(Path.GetDirectoryName(fileName), subProjectFilename);
                    }

                    if (Project.IsEnterpriseTemplateProject(fullPath)) {
                        RecursiveLoadTemplateProject(fullPath);
                    } else {
                        _htProjectFiles[projectGuidNode.InnerText] = fullPath;
                    }
                } else {
                    Log(Level.Verbose, "Skipping file reference '{0}'.", 
                        fileNode.InnerText);
                }
            }
        }

        /// <summary>
        /// Gets the project file of the project with the given unique identifier.
        /// </summary>
        /// <param name="projectGuid">The unique identifier of the project for which the project file should be retrieves.</param>
        /// <returns>
        /// The project file of the project with the given unique identifier.
        /// </returns>
        /// <exception cref="BuildException">No project with unique identifier <paramref name="projectGuid" /> could be located.</exception>
        public string GetProjectFileFromGuid(string projectGuid) {
            // locate project file using the project guid
            string projectFile = (string) _htProjectFiles[projectGuid];

            // TODO : as an emergency patch throw a build error when a GUID fails
            // to return a project file. This should be sanity checked when the 
            // HashTable is populated and not at usage time to avoid internal 
            // errors during build.
            if (projectFile == null) {
                throw new BuildException(string.Format(CultureInfo.InvariantCulture,
                    "Project with GUID '{0}' must be included for the build to" 
                    + " work.", projectGuid), Location.UnknownLocation);
            }

            return projectFile;
        }

        public ProjectBase GetProjectFromGuid(string projectGuid) {
            return (ProjectBase) _htProjects[projectGuid];
        }

        public bool Compile(string configuration) {
            Hashtable htDeps = (Hashtable) _htProjectDependencies.Clone();
            Hashtable htProjectsDone = CollectionsUtil.CreateCaseInsensitiveHashtable();
            Hashtable htFailedProjects = CollectionsUtil.CreateCaseInsensitiveHashtable();
            ArrayList failedProjects = new ArrayList();
            bool success = true;

            while (true) {
                bool compiledThisRound = false;

                foreach (ProjectBase p in _htProjects.Values) {
                    if (htProjectsDone.Contains(p.Guid)) {
                        continue;
                    }

                    if (GetProjectDependencies(p.Guid).Length == 0) {
                        bool failed = htFailedProjects.Contains(p.Guid);

                        if (!failed) {
                            // Fixup references
                            Log(Level.Verbose, "Fixing up references...");

                            foreach (Reference reference in p.References) {
                                // store original reference filename
                                string originalReference = reference.Filename;

                                if (reference.IsProjectReference) {
                                    // at the time when the reference was constructed,
                                    // the configuration was not known, so we need to
                                    // set the Filename of the reference now
                                    ProjectBase projectReference = GetProjectFromGuid(reference.Project.Guid);
                                    if (projectReference == null) {
                                        throw new BuildException(string.Format(CultureInfo.InvariantCulture, 
                                            "Unable to locate referenced project '{0}' while loading '{1}'.",
                                            reference.Name, p.Name), Location.UnknownLocation);
                                    }
                                    string outputPath = projectReference.GetOutputPath(configuration);
                                    if (outputPath == null) {
                                        throw new BuildException(string.Format(CultureInfo.InvariantCulture, 
                                            "Unable to find '{0}' configuration for project '{1}'.",
                                            configuration, projectReference.Name), Location.UnknownLocation);
                                    }
                                    reference.Filename = outputPath;
                                } else {
                                    // if the reference is an output file of
                                    // another build configuration of a project
                                    // and this output file wasn't built before
                                    // then use the output file for the current 
                                    // build configuration 
                                    //
                                    // eg. a project file might be referencing the
                                    // the debug assembly of a given project as an
                                    // assembly reference, but the projects are now 
                                    // being built in release configuration, so
                                    // instead of failing the build we use the 
                                    // release assembly of that project

                                    // Note that this was designed to intentionally 
                                    // deviate from VS.NET's building strategy.

                                    // See "Reference Configuration Matching" at http://nant.sourceforge.net/wiki/index.php/SolutionTask
                                    // for why we must always convert file references to project references

                                    // If we want a different behaviour, this 
                                    // should be controlled by a flag

                                    ProjectBase projectReference = null;

                                    if (_htOutputFiles.Contains(reference.Filename)) {
                                        projectReference = (ProjectBase) _htProjects[
                                            (string) _htOutputFiles[reference.Filename]];
                                    } else if (_outputDir != null) {
                                        // if an output directory is set, then the 
                                        // assembly reference might not have been 
                                        // resolved during Reference initialization, 
                                        // as the output file of the project might 
                                        // not have existed at that time

                                        string projectOutput = Path.Combine(
                                            _outputDir.FullName, Path.GetFileName(
                                            reference.Filename));
                                        if (_htOutputFiles.Contains(projectOutput)) {
                                            projectReference = (ProjectBase) _htProjects[
                                                (string) _htOutputFiles[projectOutput]];
                                        }
                                    }
                                        
                                    if (projectReference != null) {
                                        reference.Project = projectReference;
                                        reference.Filename = projectReference.GetOutputPath(configuration);
                                        Log(Level.Verbose, "Converted file reference to project reference: {0} -> {1}", 
                                            originalReference, projectReference.Name);
                                    }
                                }

                                // only output message when reference has actually been fixed up
                                if (originalReference != reference.Filename) {
                                    Log(Level.Verbose, "Fixed reference '{0}': {1} -> {2}.", 
                                        reference.Name, originalReference, reference.Filename);
                                }
                            }
                        }

                        try {
                            if (!_htReferenceProjects.Contains(p.Guid) && (failed || !p.Compile(configuration))) {
                                if (!failed) {
                                    Log(Level.Error, "Project '{0}' failed!", p.Name);
                                    Log(Level.Error, "Continuing build with non-dependent projects.");
                                    failedProjects.Add( p.Name );
                                }

                                success = false;
                                htFailedProjects[p.Guid] = null;

                                // mark the projects referencing this one as failed
                                foreach (ProjectBase pFailed in _htProjects.Values) {
                                    if (HasProjectDependency(pFailed.Guid, p.Guid)) {
                                        htFailedProjects[pFailed.Guid] = null;
                                    }
                                }
                            }
                        } catch ( BuildException ) {
                            // Re-throw build exceptions
                            throw;
                        } catch ( Exception e ) {
                            throw new BuildException(string.Format(CultureInfo.InvariantCulture, "Unexpected error while compiling project '{0}'", p.Name), Location.UnknownLocation, e);
                        }

                        compiledThisRound = true;

                        // remove all references to this project
                        foreach (ProjectBase pRemove in _htProjects.Values) {
                            RemoveProjectDependency(pRemove.Guid, p.Guid);
                        }
                        htProjectsDone[p.Guid] = null;
                    }
                }

                if (_htProjects.Count == htProjectsDone.Count) {
                    break;
                }
                if (!compiledThisRound) {
                    throw new BuildException("Circular dependency detected.", Location.UnknownLocation);
                }
            }

            if (failedProjects.Count > 0) {
                Log(Level.Error, string.Empty);
                Log(Level.Error, "Solution failed to build!  Failed projects were:" );
                foreach (string projectName in failedProjects)
                    Log(Level.Error, "  - " + projectName );
            }

            return success;
        }

        #endregion Public Instance Methods

        #region Protected Instance Methods

        /// <summary>
        /// Logs a message with the given priority.
        /// </summary>
        /// <param name="messageLevel">The message priority at which the specified message is to be logged.</param>
        /// <param name="message">The message to be logged.</param>
        /// <remarks>
        /// The actual logging is delegated to the underlying task.
        /// </remarks>
        protected void Log(Level messageLevel, string message) {
            if (_solutionTask != null) {
                _solutionTask.Log(messageLevel, message);
            }
        }

        /// <summary>
        /// Logs a message with the given priority.
        /// </summary>
        /// <param name="messageLevel">The message priority at which the specified message is to be logged.</param>
        /// <param name="message">The message to log, containing zero or more format items.</param>
        /// <param name="args">An <see cref="object" /> array containing zero or more objects to format.</param>
        /// <remarks>
        /// The actual logging is delegated to the underlying task.
        /// </remarks>
        protected void Log(Level messageLevel, string message, params object[] args) {
            if (_solutionTask != null) {
                _solutionTask.Log(messageLevel, message, args);
            }
        }

        protected void LoadProjectGuids(ArrayList projects, bool isReferenceProject) {
            foreach (string projectFileName in projects) {
                string projectGuid = ProjectFactory.LoadGuid(projectFileName);
                if (_htProjectFiles[projectGuid] != null) {
                    throw new BuildException(string.Format(CultureInfo.InvariantCulture,
                        "Error loading project {0}. " 
                        + " Project GUID {1} already exists! Conflicting project is {2}.", 
                        projectFileName, projectGuid, _htProjectFiles[projectGuid]));
                }
                _htProjectFiles[projectGuid] = projectFileName;
                if (isReferenceProject)
                    _htReferenceProjects[projectGuid] = null;
            }
        }

        protected void AddProjectDependency(string projectGuid, string dependencyGuid) {
            if (!_htProjectDependencies.Contains(projectGuid)) {
                _htProjectDependencies[projectGuid] = CollectionsUtil.CreateCaseInsensitiveHashtable();
            }

            ((Hashtable) _htProjectDependencies[projectGuid])[dependencyGuid] = null;
        }

        /// <summary>
        /// Loads the projects from the file system and stores them in an 
        /// instance variable.
        /// </summary>
        /// <param name="gacCache"><see cref="GacCache" /> instance to use to determine whether an assembly is located in the Global Assembly Cache.</param>
        /// <param name="refResolver"><see cref="ReferencesResolver" /> instance to use to determine location and references of assemblies.</param>
        /// <exception cref="BuildException">A project GUID in the solution file does not match the actual GUID of the project in the project file.</exception>
        protected void LoadProjects(GacCache gacCache, ReferencesResolver refResolver) {
            Log(Level.Verbose, "Loading projects...");

            FileSet excludes = _solutionTask.ExcludeProjects;

            // _htProjectFiles contains project GUIDs read from the sln file as 
            // keys and the corresponding full path to the project file as the 
            // value
            foreach (DictionaryEntry de in _htProjectFiles) {
                string projectPath = (string) de.Value;

                // determine whether project is on case-sensitive filesystem,
                bool caseSensitive = PlatformHelper.IsVolumeCaseSensitive(projectPath);

                // indicates whether the project should be skipped (excluded)
                bool skipProject = false;

                // check whether project should be excluded from build
                foreach (string excludedProjectFile in excludes.FileNames) {
                    if (string.Compare(excludedProjectFile, projectPath, caseSensitive, CultureInfo.InvariantCulture) == 0) {
                        Log(Level.Verbose, "Excluding project '{0}'.", 
                            projectPath);
                        // do not load project
                        skipProject = true;
                        // we have a match, so quit looking
                        break;
                    }
                }

                if (skipProject) {
                    // project was excluded, move on to next project
                    continue;
                }

                Log(Level.Verbose, "Loading project '{0}'.", projectPath);
                ProjectBase p = ProjectFactory.LoadProject(this, _solutionTask, _tfc, gacCache, refResolver, _outputDir, projectPath);
                if (p.Guid == null || p.Guid == string.Empty) {
                    p.Guid = FindGuidFromPath(projectPath);
                }

                // If the project GUID from the sln file doesn't match the project GUID
                // from the project file we will run into problems. Alert the user to fix this
                // as it is basically a corruption probably caused by user manipulation of the sln
                // included projects. I.e. copy and paste issue.
                if (!p.Guid.Equals(de.Key.ToString())) {
                    throw new BuildException(string.Format(CultureInfo.InvariantCulture,
                        "GUID corruption detected for project '{0}'. GUID values" 
                        + " in project file and solution file do not match ('{1}'" 
                        + " and '{2}'). Please correct this manually.", p.Name, 
                        p.Guid, de.Key.ToString()), Location.UnknownLocation);
                }

                // set project build configuration
                SetProjectBuildConfiguration(p);

                // add project to hashtable
                _htProjects[de.Key] = p;
            }
        }

        protected void GetDependenciesFromProjects() {
            Log(Level.Verbose, "Gathering additional dependencies...");

            // first get all of the output files
            foreach (DictionaryEntry de in _htProjects) {
                string projectGuid = (string) de.Key;
                ProjectBase p = (ProjectBase) de.Value;

                foreach (string configuration in p.Configurations) {
                    _htOutputFiles[p.GetOutputPath(configuration)] = projectGuid;
                }
            }

            // if one of output files resides in reference search path - circle began
            // we must build project with that outputFile before projects referencing it
            // (similar to project dependency) VS.NET 7.0/7.1 do not address this problem

            // build list of output which reside in such folders
            Hashtable outputsInAssemblyFolders = CollectionsUtil.CreateCaseInsensitiveHashtable();

            foreach (DictionaryEntry de in _htOutputFiles) {
                string outputfile = (string)de.Key;
                string folder = Path.GetDirectoryName(outputfile);

                if (_solutionTask.AssemblyFolders.DirectoryNames.Contains(folder) || _solutionTask.DefaultAssemblyFolders.DirectoryNames.Contains(folder)) {
                    outputsInAssemblyFolders[Path.GetFileName(outputfile)] = de.Value;
                }
            }

            // build the dependency list
            foreach (DictionaryEntry de in _htProjects) {
                string projectGuid = (string) de.Key;
                ProjectBase project = (ProjectBase) de.Value;

                foreach (Reference reference in project.References) {
                    if (reference.IsProjectReference) {
                        AddProjectDependency(projectGuid, reference.Project.Guid);
                    } else if (_htOutputFiles.Contains(reference.Filename)) {
                        AddProjectDependency(projectGuid, (string) _htOutputFiles[reference.Filename]);
                    } else if (outputsInAssemblyFolders.Contains(Path.GetFileName(reference.Filename))) {
                        AddProjectDependency(projectGuid, (string) outputsInAssemblyFolders[Path.GetFileName(reference.Filename)]);
                    }
                }
            }
        }

        /// <summary>
        /// Translates a project path, in the form of a relative file path or
        /// a URL, to an absolute file path.
        /// </summary>
        /// <param name="solutionDir">The directory of the solution.</param>
        /// <param name="projectPath">The project path to translate to an absolute file path.</param>
        /// <returns>
        /// The project path translated to an absolute file path.
        /// </returns>
        protected string TranslateProjectPath(string solutionDir, string projectPath) {
            if (solutionDir == null) {
                throw new ArgumentNullException("solutionDir");
            }
            if (projectPath == null) {
                throw new ArgumentNullException("projectPath");
            }

            string translatedPath = null;

            // translate URLs to physical paths if using a webmap
            string map = WebMaps.FindBestMatch(projectPath);
            if (map != null) {
                Log(Level.Debug, "Found webmap match '{0}' for '{1}.", 
                    map, projectPath);
                translatedPath = map;
            } else {
                translatedPath = projectPath;
            }

            try {
                Uri uri = new Uri(translatedPath);
                if (uri.Scheme == Uri.UriSchemeFile) {
                    translatedPath = Path.Combine(solutionDir, uri.LocalPath);
                } else {
                    if (!_solutionTask.EnableWebDav) {
                        throw new BuildException(string.Format(CultureInfo.InvariantCulture,
                            "Cannot build web project '{0}'.  Please use" 
                            + " <webmap> to map the given URL to a project-relative" 
                            + " path, or specify enablewebdav=\"true\" on the" 
                            + " <solution> task element to use WebDAV.", translatedPath));
                    }
                }
            } catch (UriFormatException) {
                translatedPath = Path.Combine(solutionDir, translatedPath);
            }

            return translatedPath;
        }

        #endregion Protected Instance Methods

        #region Private Instance Methods

        private void RemoveProjectDependency(string projectGuid, string dependencyGuid) {
            if (!_htProjectDependencies.Contains(projectGuid)) {
                return;
            }

            ((Hashtable) _htProjectDependencies[projectGuid]).Remove(dependencyGuid);
        }

        private bool HasProjectDependency(string projectGuid, string dependencyGuid) {
            if (!_htProjectDependencies.Contains(projectGuid)) {
                return false;
            }

            return ((Hashtable) _htProjectDependencies[projectGuid]).Contains(dependencyGuid);
        }

        private string[] GetProjectDependencies(string projectGuid) {
            if (!_htProjectDependencies.Contains(projectGuid)) {
                return new string[0];
            }

            return (string[]) new ArrayList(((Hashtable) _htProjectDependencies[projectGuid]).Keys).ToArray(typeof(string));
        }

        private void AddProjectBuildConfiguration(string projectGuid, string configuration) {
            if (!_htProjectBuildConfigurations.Contains(projectGuid)) {
                _htProjectBuildConfigurations[projectGuid] = CollectionsUtil.CreateCaseInsensitiveHashtable();
            }

            ((Hashtable) _htProjectBuildConfigurations[projectGuid])[configuration] = null;
        }

        private void SetProjectBuildConfiguration(ProjectBase project) {
            if (!_htProjectBuildConfigurations.Contains(project.Guid)) {
                // project was not loaded from solution file, so there's no
                // project configuration section available, so we'll consider 
                // all project configurations as valid build configurations
                project.BuildConfigurations.Clear();
                foreach (string configuration in project.ProjectConfigurations.Keys) {
                    project.BuildConfigurations[configuration] = project.ProjectConfigurations[configuration];
                }
            } else {
                // project was loaded from solution file, so only add build
                // configurations that were listed in project configuration
                // section
                Hashtable projectBuildConfigurations = (Hashtable) _htProjectBuildConfigurations[project.Guid];
                foreach (string configuration in projectBuildConfigurations.Keys) {
                    if (project.ProjectConfigurations.ContainsKey(configuration)) {
                        project.BuildConfigurations[configuration] = project.ProjectConfigurations[configuration];
                    }
                }
            }
        }

        private string FindGuidFromPath(string projectPath) {
            foreach (DictionaryEntry de in _htProjectFiles) {
                string guid = (string) de.Key;
                string path = (string) de.Value;
                if (string.Compare(path, projectPath, true, CultureInfo.InvariantCulture) == 0) {
                    return guid;
                }
            }
            return "";
        }

        #endregion Private Instance Methods

        #region Private Instance Fields

        private FileInfo _file;
        private Hashtable _htProjectFiles;
        private Hashtable _htProjects;
        private Hashtable _htProjectDirectories;
        private Hashtable _htProjectDependencies;
        private Hashtable _htProjectBuildConfigurations;
        private Hashtable _htOutputFiles;
        private Hashtable _htReferenceProjects;
        private SolutionTask _solutionTask;
        private WebMapCollection _webMaps;
        private DirectoryInfo _outputDir;
        private TempFileCollection _tfc;

        #endregion Private Instance Fields
    }
}